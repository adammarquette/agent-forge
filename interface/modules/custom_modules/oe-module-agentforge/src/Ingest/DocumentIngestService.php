<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Ingest;

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
final class DocumentIngestService
{
    public function __construct(private readonly AgentForgeGlobalConfig $config)
    {
    }

    /**
     * Scans documents newer than the watermark whose category is mapped to a docType, forwards each to the
     * sidecar, and advances the watermark. Fails closed: with no ingest URI, no category map, or no token,
     * it does nothing rather than forwarding documents that cannot be ingested.
     */
    public function run(): void
    {
        $ingestUri = $this->config->getIngestUri();
        $categoryMap = $this->config->getIngestCategoryMap();
        if ($ingestUri === null || $categoryMap === []) {
            return;
        }

        $token = $this->resolveBearerToken();
        if ($token === null) {
            return;
        }

        $watermark = $this->config->getIngestWatermark();
        $rows = $this->fetchNewDocuments($watermark, array_keys($categoryMap));

        $highestId = $watermark;
        foreach ($rows as $row) {
            $documentId = (int) $row['id'];
            $docType = $categoryMap[(string) $row['category_id']] ?? null;
            if ($docType !== null && $this->forwardDocument($ingestUri, $token, $row, $docType)) {
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

        // documents<->category is the categories_to_documents join, not a column on documents.
        // VERIFY: table/column names against this fork's schema (documents.id/foreign_id/mimetype/deleted,
        // categories_to_documents.category_id/document_id).
        $placeholders = implode(',', array_fill(0, count($categoryIds), '?'));
        $sql = 'SELECT d.`id`, d.`foreign_id`, d.`mimetype`, ctd.`category_id` '
            . 'FROM `documents` d '
            . 'JOIN `categories_to_documents` ctd ON ctd.`document_id` = d.`id` '
            . 'WHERE d.`id` > ? AND d.`deleted` = 0 AND ctd.`category_id` IN (' . $placeholders . ') '
            . 'ORDER BY d.`id` ASC';

        /** @var list<array<string, mixed>> $rows */
        $rows = QueryUtils::fetchRecords($sql, array_merge([$watermark], $categoryIds));
        return $rows;
    }

    /**
     * @param array<string, mixed> $row
     */
    private function forwardDocument(string $ingestUri, string $token, array $row, string $docType): bool
    {
        $documentId = (int) $row['id'];
        $patientId = (string) $row['foreign_id'];
        $mediaType = (string) ($row['mimetype'] ?? 'application/octet-stream');

        // VERIFY: reading the (decrypted) bytes and the FHIR DocumentReference id (the document uuid) against
        // this fork's Document class API - method names below are the expected shape, confirm them.
        $document = new \Document($documentId);
        $content = $document->get_data();
        $documentReferenceId = \OpenEMR\Common\Uuid\UuidRegistry::uuidToString($document->get_uuid());
        if (!is_string($content) || $content === '' || $documentReferenceId === '') {
            return false;
        }

        $body = [
            'patientId' => $patientId,
            'documentReferenceId' => $documentReferenceId,
            'docType' => $docType,
            // curl multipart: a CURLFile from an in-memory string requires a temp file; simplest is to write
            // the bytes to a temp path. VERIFY the sidecar accepts the field name 'file'.
            'file' => $this->asCurlFile($content, (string) ($row['name'] ?? 'document'), $mediaType),
        ];

        $curl = curl_init($ingestUri);
        curl_setopt_array($curl, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => $body,
            CURLOPT_HTTPHEADER => ['Authorization: Bearer ' . $token],
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 120,
        ]);
        curl_exec($curl);
        $status = (int) curl_getinfo($curl, CURLINFO_HTTP_CODE);
        curl_close($curl);

        // 200 = ingested / already on file; anything else is retried next run.
        return $status === 200;
    }

    private function asCurlFile(string $content, string $filename, string $mediaType): \CURLFile
    {
        $tmp = tempnam(sys_get_temp_dir(), 'agf');
        file_put_contents($tmp, $content);
        return new \CURLFile($tmp, $mediaType, $filename);
    }

    /**
     * The transient bearer token for the sidecar call.
     *
     * TODO (agent-forge#44): mint a short-lived OpenEMR access token via the client_credentials grant using
     * a registered confidential SYSTEM client (private_key_jwt client assertion - OpenEMR's backend-services
     * flow), cache it for this run, and discard it. Nothing is stored except the client's key material in
     * config; the token itself stays transient. Until that is wired, an operator-supplied token in
     * AGENTFORGE_INGEST_TOKEN lets the scan + forward path be exercised end-to-end.
     */
    private function resolveBearerToken(): ?string
    {
        $token = getenv('AGENTFORGE_INGEST_TOKEN');
        return is_string($token) && $token !== '' ? $token : null;
    }
}
