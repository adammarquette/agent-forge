<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Ingest;

use GuzzleHttp\Client;
use GuzzleHttp\Exception\GuzzleException;
use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;

/**
 * Forwards newly-uploaded patient documents to the AgentForge sidecar for extraction. Runs as an OpenEMR
 * Background Service (cron), pre-visit, so the clinician's copilot only reads ready facts. The front desk
 * uploads through OpenEMR's own Documents workflow; this scan is the trigger (OpenEMR has no
 * document-created event to subscribe to). reference: agent-forge#44, agent-forge-copilot#81.
 *
 * DRAFT - not yet exercised against a running OpenEMR. Points needing verification are marked "VERIFY:".
 */
final readonly class DocumentIngestService
{
    public function __construct(private AgentForgeGlobalConfig $config)
    {
    }

    /**
     * Scans documents newer than the watermark whose category is mapped to a docType, forwards each to the
     * sidecar, and advances the watermark. Fails closed: with no ingest URI or no category map, it does
     * nothing rather than forwarding documents that cannot be ingested.
     */
    public function run(): void
    {
        $ingestUri = $this->config->getIngestUri();
        $categoryMap = $this->config->getIngestCategoryMap();
        if ($ingestUri === null || $categoryMap === []) {
            return;
        }

        $watermark = $this->config->getIngestWatermark();
        $rows = $this->fetchNewDocuments($watermark, array_keys($categoryMap));

        $highestId = $watermark;
        foreach ($rows as $row) {
            $documentId = self::toInt($row['id']);
            $docType = $categoryMap[self::toStr($row['category_id'])] ?? null;
            if ($docType !== null && $this->forwardDocument($ingestUri, $row, $docType)) {
                // Only advance past a document once the sidecar has accepted it (or reported it already on
                // file); a failed forward is retried next run. The sidecar is content-hash idempotent.
                $highestId = max($highestId, $documentId);
            }
        }

        if ($highestId > $watermark) {
            $this->config->setIngestWatermark($highestId);
        }
    }

    /**
     * @param list<string> $categoryIds
     * @return list<array<string, mixed>>
     */
    private function fetchNewDocuments(int $watermark, array $categoryIds): array
    {
        if ($categoryIds === []) {
            return [];
        }

        // documents<->category is the categories_to_documents join; patient_data resolves the patient's FHIR
        // UUID from documents.foreign_id (the internal pid). The sidecar keys derived facts on the UUID, not
        // the pid, so the join is required, not cosmetic - an INNER JOIN also naturally drops any non-patient
        // documents. reference: schema verified against staging 2026-07-14; agent-forge-copilot#93.
        $placeholders = implode(',', array_fill(0, count($categoryIds), '?'));
        $sql = 'SELECT d.`id`, d.`mimetype`, d.`name`, ctd.`category_id`, pd.`uuid` AS `patient_uuid` '
            . 'FROM `documents` d '
            . 'JOIN `categories_to_documents` ctd ON ctd.`document_id` = d.`id` '
            . 'JOIN `patient_data` pd ON pd.`pid` = d.`foreign_id` '
            . 'WHERE d.`id` > ? AND d.`deleted` = 0 AND ctd.`category_id` IN (' . $placeholders . ') '
            . 'ORDER BY d.`id` ASC';

        /** @var list<array<string, mixed>> $rows */
        $rows = QueryUtils::fetchRecords($sql, array_merge([$watermark], $categoryIds));
        return $rows;
    }

    /**
     * @param array<string, mixed> $row
     */
    private function forwardDocument(string $ingestUri, array $row, string $docType): bool
    {
        $documentId = self::toInt($row['id']);
        $mediaType = self::toStr($row['mimetype'] ?? 'application/octet-stream');

        // The sidecar keys derived facts on the patient's FHIR UUID, not the internal pid - passing a pid
        // makes it read Patient/<pid>, which OpenEMR rejects. Resolved via the patient_data join above.
        // reference: Bootstrap::buildDirectPatientLaunchUrl (gitlab agent-forge#38), agent-forge-copilot#93.
        $patientUuid = $row['patient_uuid'] ?? null;
        if (!is_string($patientUuid) || $patientUuid === '') {
            return false;
        }
        $patientId = \OpenEMR\Common\Uuid\UuidRegistry::uuidToString($patientUuid);

        // VERIFY: reading the (decrypted) bytes and the FHIR DocumentReference id (the document uuid) against
        // this fork's Document class API - method names below are the expected shape, confirm them.
        $document = new \Document($documentId);
        $content = $document->get_data();
        $documentReferenceId = \OpenEMR\Common\Uuid\UuidRegistry::uuidToString($document->get_uuid());
        if (!is_string($content) || $content === '' || $documentReferenceId === '') {
            return false;
        }

        // No auth header: the sidecar's /documents/ingest trusts its private-network origin (W2-D17). ingestUri
        // is the sidecar's internal address (agent-forge-api-staging.railway.internal:8080), never public; the
        // reverse proxy does not route this path. reference: agent-forge-copilot#91, W2_ARCHITECTURE.md §15 W2-D17.
        // http_errors off + catch: a non-200 or transport failure is a no-op that gets retried next run, never
        // aborts the whole scan. The sidecar is content-hash idempotent so a retry is safe.
        try {
            $response = (new Client())->request('POST', $ingestUri, [
                'multipart' => [
                    ['name' => 'patientId', 'contents' => $patientId],
                    ['name' => 'documentReferenceId', 'contents' => $documentReferenceId],
                    ['name' => 'docType', 'contents' => $docType],
                    [
                        'name' => 'file',
                        'contents' => $content,
                        'filename' => self::toStr($row['name'] ?? 'document'),
                        'headers' => ['Content-Type' => $mediaType],
                    ],
                ],
                'timeout' => 120,
                'http_errors' => false,
            ]);
        } catch (GuzzleException) {
            return false;
        }

        // 200 = ingested / already on file; anything else is retried next run.
        return $response->getStatusCode() === 200;
    }

    // QueryUtils::fetchRecords yields mixed cells; narrow before converting - the fork forbids casting mixed.
    private static function toInt(mixed $value): int
    {
        return is_numeric($value) ? (int) $value : 0;
    }

    private static function toStr(mixed $value): string
    {
        return is_scalar($value) ? (string) $value : '';
    }
}
