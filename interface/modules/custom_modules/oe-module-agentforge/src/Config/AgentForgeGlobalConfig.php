<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Config;

use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Core\OEGlobalsBag;

final class AgentForgeGlobalConfig
{
    public const LAUNCH_URI = 'agentforge_launch_uri';
    public const ISSUER = 'agentforge_issuer';
    public const LAUNCH_MODE = 'agentforge_launch_mode';

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

    public function getIssuer(): ?string
    {
        return $this->resolve(self::ISSUER, 'AGENTFORGE_ISSUER');
    }

    public function getLaunchMode(): string
    {
        $value = OEGlobalsBag::getInstance()->getString(self::LAUNCH_MODE);
        return $value === self::LAUNCH_MODE_IFRAME ? self::LAUNCH_MODE_IFRAME : self::LAUNCH_MODE_TAB;
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

    public function getStoredIssuer(): string
    {
        return OEGlobalsBag::getInstance()->getString(self::ISSUER);
    }

    public function save(string $launchUri, string $issuer, string $launchMode): void
    {
        $this->upsert(self::LAUNCH_URI, $launchUri);
        $this->upsert(self::ISSUER, $issuer);
        $this->upsert(
            self::LAUNCH_MODE,
            $launchMode === self::LAUNCH_MODE_IFRAME ? self::LAUNCH_MODE_IFRAME : self::LAUNCH_MODE_TAB
        );
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
