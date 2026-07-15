<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Config;

use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Core\OEGlobalsBag;

final class AgentForgeGlobalConfig
{
    public const LAUNCH_URI = 'agentforge_launch_uri';
    public const AGENDA_LAUNCH_URI = 'agentforge_agenda_launch_uri';
    public const ISSUER = 'agentforge_issuer';
    public const LAUNCH_MODE = 'agentforge_launch_mode';

    /** The sidecar's POST /documents/ingest endpoint the ingestion cron calls. */
    public const INGEST_URI = 'agentforge_ingest_uri';

    /** Last `documents.id` the ingestion cron has forwarded (the scan watermark). */
    public const INGEST_WATERMARK = 'agentforge_ingest_watermark';

    /**
     * JSON object mapping an OpenEMR document category id (string) to a sidecar docType
     * ("lab_pdf" | "intake_form"), e.g. {"12":"lab_pdf","15":"intake_form"}. The cron only
     * forwards documents whose category is in this map, so unrelated uploads are ignored.
     */
    public const INGEST_CATEGORY_MAP = 'agentforge_ingest_category_map';

    /** Whether the module injects the top-nav "Daily Agenda" tab (default on). */
    public const SHOW_AGENDA_MENU = 'agentforge_show_agenda_menu';

    /**
     * Cross-origin iframe modal (dlgopen()) - the original launch mechanism.
     * Only works reliably if the sidecar is same-site with OpenEMR (see
     * Documentation/agent-forge/IFRAME_REVERT.md); otherwise the OAuth
     * round-trip loses the session (agent-forge#21).
     */
    public const LAUNCH_MODE_IFRAME = 'iframe';

    /**
     * Real top-level browser tab (window.open()) plus the EHR-launch bridge
     * cookie - works regardless of whether the sidecar is same-site, at the
     * cost of a separate browser window/tab instead of an inline modal.
     * Default, since it's the mode that works without a same-site sidecar.
     */
    public const LAUNCH_MODE_TAB = 'tab';

    public function getLaunchUri(): ?string
    {
        return $this->resolve(self::LAUNCH_URI, 'AGENTFORGE_LAUNCH_URI');
    }

    /**
     * The roster/Daily-Agenda launch endpoint - distinct from getLaunchUri()
     * because the sidecar serves the single-patient launch and the roster
     * launch from different paths (.../agentforge/launch vs
     * .../agentforge/agenda/launch); one shared URI would send the per-patient
     * "Launch AgentForge" button to the roster endpoint (which ignores patient
     * context), rendering the Daily Agenda instead of the patient chat.
     * agenda-launch.php falls back to getLaunchUri() when this is unset, so an
     * unconfigured install keeps its previous single-URI behavior.
     */
    public function getAgendaLaunchUri(): ?string
    {
        return $this->resolve(self::AGENDA_LAUNCH_URI, 'AGENTFORGE_AGENDA_LAUNCH_URI');
    }

    public function getIssuer(): ?string
    {
        return $this->resolve(self::ISSUER, 'AGENTFORGE_ISSUER');
    }

    /** The sidecar ingest endpoint, e.g. https://.../agentforge/documents/ingest. */
    public function getIngestUri(): ?string
    {
        return $this->resolve(self::INGEST_URI, 'AGENTFORGE_INGEST_URI');
    }

    /** The highest `documents.id` already forwarded to the sidecar (0 when never run). */
    public function getIngestWatermark(): int
    {
        return (int) OEGlobalsBag::getInstance()->getString(self::INGEST_WATERMARK);
    }

    public function setIngestWatermark(int $lastDocumentId): void
    {
        $this->upsert(self::INGEST_WATERMARK, (string) $lastDocumentId);
    }

    /**
     * Category-id => docType map. Returns an empty array when unset or malformed, which makes the
     * cron a no-op (fail closed) rather than forwarding documents with an unknown schema.
     *
     * @return array<string, string>
     */
    public function getIngestCategoryMap(): array
    {
        $raw = $this->resolve(self::INGEST_CATEGORY_MAP, 'AGENTFORGE_INGEST_CATEGORY_MAP');
        if ($raw === null) {
            return [];
        }

        $decoded = json_decode($raw, true);
        if (!is_array($decoded)) {
            return [];
        }

        $map = [];
        foreach ($decoded as $categoryId => $docType) {
            if (is_string($docType) && ($docType === 'lab_pdf' || $docType === 'intake_form')) {
                $map[(string) $categoryId] = $docType;
            }
        }

        return $map;
    }

    public function getLaunchMode(): string
    {
        $value = OEGlobalsBag::getInstance()->getString(self::LAUNCH_MODE);
        return $value === self::LAUNCH_MODE_IFRAME ? self::LAUNCH_MODE_IFRAME : self::LAUNCH_MODE_TAB;
    }

    /**
     * Whether to inject the top-nav "Daily Agenda" tab. Defaults to true so an
     * unconfigured install keeps the tab it has today; only an explicit save of
     * the (unchecked) toggle hides it.
     */
    public function isAgendaMenuEnabled(): bool
    {
        return OEGlobalsBag::getInstance()->getBoolean(self::SHOW_AGENDA_MENU, true);
    }

    /**
     * The raw saved override, ignoring the env var fallback - used to populate
     * the settings form so an unmodified env-var-derived value never gets
     * silently written back into the globals table as a permanent override.
     */
    public function getStoredLaunchUri(): string
    {
        return OEGlobalsBag::getInstance()->getString(self::LAUNCH_URI);
    }

    public function getStoredAgendaLaunchUri(): string
    {
        return OEGlobalsBag::getInstance()->getString(self::AGENDA_LAUNCH_URI);
    }

    public function getStoredIssuer(): string
    {
        return OEGlobalsBag::getInstance()->getString(self::ISSUER);
    }

    public function save(
        string $launchUri,
        string $agendaLaunchUri,
        string $issuer,
        string $launchMode,
        bool $showAgendaMenu
    ): void {
        $this->upsert(self::LAUNCH_URI, $launchUri);
        $this->upsert(self::AGENDA_LAUNCH_URI, $agendaLaunchUri);
        $this->upsert(self::ISSUER, $issuer);
        $this->upsert(
            self::LAUNCH_MODE,
            $launchMode === self::LAUNCH_MODE_IFRAME ? self::LAUNCH_MODE_IFRAME : self::LAUNCH_MODE_TAB
        );
        $this->upsert(self::SHOW_AGENDA_MENU, $showAgendaMenu ? '1' : '0');
    }

    private function upsert(string $globalKey, string $value): void
    {
        QueryUtils::sqlStatementThrowException(
            'INSERT INTO `globals` (`gl_name`, `gl_value`) VALUES (?, ?) '
                . 'ON DUPLICATE KEY UPDATE `gl_value` = ?',
            [$globalKey, $value, $value]
        );
        OEGlobalsBag::getInstance()->set($globalKey, $value);
    }

    private function resolve(string $globalKey, string $envVar): ?string
    {
        $value = OEGlobalsBag::getInstance()->getString($globalKey);
        if ($value !== '') {
            return $value;
        }

        $envValue = getenv($envVar);
        return is_string($envValue) && $envValue !== '' ? $envValue : null;
    }
}
