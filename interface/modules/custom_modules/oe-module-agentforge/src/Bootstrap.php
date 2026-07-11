<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge;

use OpenEMR\Common\Acl\AclMain;
use OpenEMR\Events\PatientDemographics\RenderEvent;
use OpenEMR\FHIR\Config\ServerConfig;
use OpenEMR\FHIR\SMART\SMARTLaunchToken;
use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;
use Symfony\Component\EventDispatcher\EventDispatcherInterface;

final readonly class Bootstrap
{
    public function __construct(
        private EventDispatcherInterface $eventDispatcher,
        private AgentForgeLaunchService $launchService = new AgentForgeLaunchService(),
    ) {
    }

    public function subscribeToEvents(): void
    {
        $this->eventDispatcher->addListener(
            RenderEvent::EVENT_SECTION_LIST_RENDER_AFTER,
            $this->renderLaunchButton(...)
        );
    }

    public function renderLaunchButton(RenderEvent $event): void
    {
        if (!AclMain::aclCheckCore('patients', 'demo')) {
            return;
        }

        $pid = $event->getPid();
        if (!is_numeric($pid) || (int) $pid <= 0) {
            return;
        }

        $launchToken = new SMARTLaunchToken();
        $launchToken->setPatient((string) $pid);
        $launchToken->setIntent(SMARTLaunchToken::INTENT_PATIENT_DEMOGRAPHICS_DIALOG);

        $serializedToken = $launchToken->serialize();
        if (!is_string($serializedToken)) {
            return;
        }

        $issuer = (new ServerConfig())->getFhirUrl();
        $launchUri = getenv('AGENTFORGE_LAUNCH_URI')
            ?: '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';
        $launchUrl = $this->launchService->buildLaunchUrl($serializedToken, $issuer, $launchUri, (string) $pid);

        // Modal-with-iframe via dlgopen(..., {allowExternal: true}) instead of a
        // plain <a href> full-page redirect, matching the same pattern OpenEMR's
        // own native SMART launch button uses (library/js/utility.js's
        // .smart-launch-btn handler) - the cross-origin hop happens inside the
        // iframe, the top-level OpenEMR page is never navigated away from.
        //
        // Load-failure detection: dlgopen has no built-in signal for iframe
        // content failing to load (checked library/dialog.js - only script/link
        // dependency loading has onerror handling, not the content iframe
        // itself), and cross-origin navigation inside the iframe means this
        // page's JS can't inspect what actually rendered there once the
        // redirect leaves same-origin. buildLaunchButtonMarkup()'s timeout is a
        // generic, origin-agnostic safety net - it fires purely on "still open
        // after N seconds", not on detecting the specific failure - so it still
        // helps even when the failure is a silent one (e.g. sidecar CSP
        // frame-ancestors rejecting the frame outright, agent-forge#10).
        echo $this->launchService->buildLaunchButtonMarkup($launchUrl);
    }
}
