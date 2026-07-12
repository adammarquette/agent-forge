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
     * $launchMode selects which click behavior buildLaunchHeaderScript()
     * uses (AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME or ::LAUNCH_MODE_TAB) -
     * see that method's doc comment for the tradeoffs.
     *
     * In tab mode, $launchUrl is public/patient-launch.php's own trigger URL
     * (or public/agenda-launch.php's), not the sidecar URL directly - that
     * endpoint builds the actual sidecar launch URL and sets the EHR-launch
     * bridge cookie itself, as a fresh request with no prior output. In
     * iframe mode, $launchUrl is the sidecar launch URL directly, built
     * inline by the caller - no bridge cookie involved.
     *
     * The URL travels via a data attribute rather than a captured JS
     * variable, since PageHeadingRenderEvent's action buttons are rendered
     * through OpenEMR's generic BaseActionButtonHelper/twig template, which
     * has no per-button script slot - buildLaunchHeaderScript() reads it back
     * off the element at click time.
     *
     * $windowName (tab mode only) scopes the opened tab (typically
     * per-patient, e.g. "agentforge-launch-123") so switching patients opens
     * a separate tab rather than replacing an already-open conversation,
     * while re-launching the same patient refocuses their existing tab. Not
     * used in iframe mode - omit it there.
     */
    public function buildLaunchActionButton(string $launchUrl, string $launchMode, ?string $windowName = null): BaseActionButtonHelper
    {
        $attributes = [
            'data-launch-url' => $launchUrl,
            'data-launch-mode' => $launchMode,
        ];
        if ($windowName !== null) {
            $attributes['data-window-name'] = $windowName;
        }

        return new BaseActionButtonHelper([
            'id' => 'agentforge-launch-btn',
            'title' => xl('Launch AgentForge'),
            'displayText' => xlt('AgentForge'),
            'iconClass' => 'fa fa-fw fa-comment-medical',
            'anchorClasses' => ['agentforge-launch-action'],
            'attributes' => $attributes,
            'clickHandlerFunctionName' => 'agentforgeHeaderLaunch',
        ]);
    }

    /**
     * Renders the click handler + load-failure warning as a single HTML/JS
     * blob, injected into the page heading's title-nav area via
     * PageHeadingRenderEvent::appendTitleNavContent().
     *
     * Branches at click time on the button's data-launch-mode attribute
     * (AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME / ::LAUNCH_MODE_TAB),
     * configurable per Manage Modules settings (see moduleConfig.php):
     *
     * - iframe: dlgopen()'s modal iframe, matching OpenEMR's own native SMART
     *   launch button pattern. Only works reliably if the sidecar is
     *   same-site with OpenEMR - see Documentation/agent-forge/IFRAME_REVERT.md.
     *   Load-failure detection polls for the iframe element after a
     *   timeout, since dlgopen() has no built-in load-failure signal.
     * - tab: a real top-level browser tab (window.open()), needed when the
     *   sidecar is cross-site - the EHR-launch OAuth round-trip then relies
     *   on a SameSite=Lax bridge cookie
     *   (SessionUtil::setEhrLaunchBridgeCookie()) to recover the session,
     *   and Lax's cross-site exception only covers top-level navigations,
     *   not iframes. Load-failure detection checks immediately whether
     *   window.open() returned a live window, since a cross-origin popup's
     *   content can't be inspected after the fact (same-origin policy) -
     *   the real failure mode here is the browser's popup blocker, not a
     *   slow-loading iframe.
     *
     * Pulled out of Bootstrap::renderLaunchButton() as pure string templating
     * (no ACL/session/DB dependency) so it's unit-testable without needing a
     * live authenticated session - renderLaunchButton() itself still needs one
     * for its ACL check and is covered separately.
     */
    public function buildLaunchHeaderScript(): string
    {
        $iframeWarningText = js_escape(xl('AgentForge is taking longer than expected to load. It may be temporarily unavailable.'));
        $tabWarningText = js_escape(xl('AgentForge could not be opened. Please allow pop-ups for this site and try again.'));
        $jsButtonTitle = js_escape(xl('Launch AgentForge'));

        return <<<HTML
        <div id="agentforge-launch-warning" class="alert alert-warning small p-1 mb-0 position-absolute" style="display:none; right:0; top:100%; z-index:1000; white-space:nowrap;"></div>
        <script>
        function agentforgeHeaderLaunch() {
            var btn = document.getElementById("agentforge-launch-btn");
            var warningEl = document.getElementById("agentforge-launch-warning");
            var launchUrl = btn ? btn.getAttribute("data-launch-url") : null;
            var launchMode = btn ? btn.getAttribute("data-launch-mode") : null;
            if (!launchUrl) {
                return;
            }
            warningEl.style.display = "none";

            if (launchMode === "iframe") {
                warningEl.textContent = {$iframeWarningText};
                dlgopen(
                    launchUrl, "agentforge-launch", "modal-full", window.top.innerHeight,
                    "", {$jsButtonTitle}, {allowExternal: true}
                );
                setTimeout(function () {
                    if (window.top.document.querySelector('[name="agentforge-launch"]')) {
                        warningEl.style.display = "block";
                    }
                }, 8000);
                return;
            }

            warningEl.textContent = {$tabWarningText};
            var windowName = btn ? btn.getAttribute("data-window-name") : null;
            if (!windowName) {
                return;
            }
            var win = window.open(launchUrl, windowName);
            if (!win || win.closed || typeof win.closed === "undefined") {
                warningEl.style.display = "block";
                return;
            }
            win.focus();
        }
        </script>
        HTML;
    }
}
