<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Launch;

final class AgentForgeLaunchService
{
    public function buildLaunchUrl(
        string $launchToken,
        string $issuer,
        string $launchUri,
        ?string $patientId = null
    ): string {
        $query = http_build_query([
            'launch' => $launchToken,
            'iss' => $issuer,
            'aud' => $issuer,
            'patient' => $patientId,
        ], '', '&', PHP_QUERY_RFC3986);

        return $launchUri . '?' . rtrim($query, '&');
    }

    /**
     * Renders the launch button + its click handler as a single HTML/JS blob.
     *
     * Pulled out of Bootstrap::renderLaunchButton() as pure string templating
     * (no ACL/session/DB dependency) so it's unit-testable without needing a
     * live authenticated session - renderLaunchButton() itself still needs one
     * for its ACL check and is covered separately.
     */
    public function buildLaunchButtonMarkup(string $launchUrl): string
    {
        $buttonLabel = xlt('Launch AgentForge');
        $warningText = xlt('AgentForge is taking longer than expected to load. It may be temporarily unavailable.');
        $jsLaunchUrl = js_escape($launchUrl);
        $jsButtonTitle = js_escape(xl('Launch AgentForge'));

        return <<<HTML
        <section class="card mb-2">
        <div class="p-2">
        <button type="button" id="agentforge-launch-btn" class="btn btn-sm btn-primary">{$buttonLabel}</button>
        <div id="agentforge-launch-warning" class="text-warning small mt-1" style="display:none;">{$warningText}</div>
        </div>
        </section>
        <script>
        document.getElementById("agentforge-launch-btn").addEventListener("click", function () {
            var warningEl = document.getElementById("agentforge-launch-warning");
            warningEl.style.display = "none";
            dlgopen(
                {$jsLaunchUrl}, "agentforge-launch", "modal-full", window.top.innerHeight,
                "", {$jsButtonTitle}, {allowExternal: true}
            );
            setTimeout(function () {
                if (window.top.document.querySelector('[name="agentforge-launch"]')) {
                    warningEl.style.display = "block";
                }
            }, 8000);
        });
        </script>
        HTML;
    }
}
