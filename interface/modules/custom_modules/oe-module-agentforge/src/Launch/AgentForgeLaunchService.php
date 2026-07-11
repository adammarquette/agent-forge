<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Launch;

use OpenEMR\Events\UserInterface\BaseActionButtonHelper;
use OpenEMR\FHIR\SMART\SMARTLaunchToken;

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
     * Builds the launch URL for the "Day's Agenda" entry point: a
     * main.tab-intent launch with no patient scoped. The sidecar resolves
     * the day's roster itself (FHIR Appointment search filtered to the
     * launching clinician's own identity), so unlike the per-patient launch
     * this carries no patient/roster data in the token at all - just enough
     * for the sidecar to know it's a main-tab, not-patient-scoped launch.
     *
     * Pure string/token templating (no ACL/session/DB dependency), same
     * rationale as buildLaunchUrl() - unit-testable without a live session.
     */
    public function buildAgendaLaunchUrl(string $issuer, string $launchUri): ?string
    {
        $launchToken = new SMARTLaunchToken();
        $launchToken->setIntent(SMARTLaunchToken::INTENT_MAIN_TAB);

        $serializedToken = $launchToken->serialize();
        if (!is_string($serializedToken)) {
            return null;
        }

        return $this->buildLaunchUrl($serializedToken, $issuer, $launchUri);
    }

    /**
     * Builds the page-heading action button (top-right nav slot, next to the
     * expand/collapse and help icons) that triggers the AgentForge launch.
     *
     * The launch URL travels via a data attribute rather than a captured JS
     * variable, since PageHeadingRenderEvent's action buttons are rendered
     * through OpenEMR's generic BaseActionButtonHelper/twig template, which
     * has no per-button script slot - buildLaunchHeaderScript() reads it back
     * off the element at click time.
     */
    public function buildLaunchActionButton(string $launchUrl): BaseActionButtonHelper
    {
        return new BaseActionButtonHelper([
            'id' => 'agentforge-launch-btn',
            'title' => xl('Launch AgentForge'),
            'displayText' => xlt('AgentForge'),
            'iconClass' => 'fa fa-fw fa-comment-medical',
            'anchorClasses' => ['agentforge-launch-action'],
            'attributes' => ['data-launch-url' => $launchUrl],
            'clickHandlerFunctionName' => 'agentforgeHeaderLaunch',
        ]);
    }

    /**
     * Renders the click handler + load-failure warning as a single HTML/JS
     * blob, injected into the page heading's title-nav area via
     * PageHeadingRenderEvent::appendTitleNavContent().
     *
     * Pulled out of Bootstrap::renderLaunchButton() as pure string templating
     * (no ACL/session/DB dependency) so it's unit-testable without needing a
     * live authenticated session - renderLaunchButton() itself still needs one
     * for its ACL check and is covered separately.
     */
    public function buildLaunchHeaderScript(): string
    {
        $warningText = xlt('AgentForge is taking longer than expected to load. It may be temporarily unavailable.');
        $jsButtonTitle = js_escape(xl('Launch AgentForge'));

        return <<<HTML
        <div id="agentforge-launch-warning" class="alert alert-warning small p-1 mb-0 position-absolute" style="display:none; right:0; top:100%; z-index:1000; white-space:nowrap;">{$warningText}</div>
        <script>
        function agentforgeHeaderLaunch() {
            var btn = document.getElementById("agentforge-launch-btn");
            var warningEl = document.getElementById("agentforge-launch-warning");
            var launchUrl = btn ? btn.getAttribute("data-launch-url") : null;
            if (!launchUrl) {
                return;
            }
            warningEl.style.display = "none";
            dlgopen(
                launchUrl, "agentforge-launch", "modal-full", window.top.innerHeight,
                "", {$jsButtonTitle}, {allowExternal: true}
            );
            setTimeout(function () {
                if (window.top.document.querySelector('[name="agentforge-launch"]')) {
                    warningEl.style.display = "block";
                }
            }, 8000);
        }
        </script>
        HTML;
    }
}
