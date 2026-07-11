<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge;

use OpenEMR\Common\Acl\AclMain;
use OpenEMR\Common\Session\SessionWrapperFactory;
use OpenEMR\Events\UserInterface\PageHeadingRenderEvent;
use OpenEMR\FHIR\Config\ServerConfig;
use OpenEMR\FHIR\SMART\SMARTLaunchToken;
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;
use Symfony\Component\EventDispatcher\EventDispatcherInterface;

final readonly class Bootstrap
{
    /**
     * The demographics page's OemrUI page_id (interface/patient_file/summary/demographics.php),
     * used to scope the launch button to that page only - PageHeadingRenderEvent
     * fires on every OemrUI-rendered page, not just the patient chart.
     */
    private const DEMOGRAPHICS_PAGE_ID = 'core.mrd';

    public function __construct(
        private EventDispatcherInterface $eventDispatcher,
        private AgentForgeLaunchService $launchService = new AgentForgeLaunchService(),
        private AgentForgeGlobalConfig $config = new AgentForgeGlobalConfig(),
    ) {
    }

    public function subscribeToEvents(): void
    {
        $this->eventDispatcher->addListener(
            PageHeadingRenderEvent::EVENT_PAGE_HEADING_RENDER,
            $this->renderLaunchButton(...)
        );
    }

    public function renderLaunchButton(PageHeadingRenderEvent $event): PageHeadingRenderEvent
    {
        if ($event->getPageId() !== self::DEMOGRAPHICS_PAGE_ID) {
            return $event;
        }

        if (!AclMain::aclCheckCore('patients', 'demo')) {
            return $event;
        }

        $pid = SessionWrapperFactory::getInstance()->getActiveSession()->get('pid');
        if (!is_numeric($pid) || (int) $pid <= 0) {
            return $event;
        }

        $launchToken = new SMARTLaunchToken();
        $launchToken->setPatient((string) $pid);
        $launchToken->setIntent(SMARTLaunchToken::INTENT_PATIENT_DEMOGRAPHICS_DIALOG);

        $serializedToken = $launchToken->serialize();
        if (!is_string($serializedToken)) {
            return $event;
        }

        $issuer = $this->config->getIssuer() ?? (new ServerConfig())->getFhirUrl();
        $launchUri = $this->config->getLaunchUri()
            ?? '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';
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
        // redirect leaves same-origin. buildLaunchHeaderScript()'s timeout is a
        // generic, origin-agnostic safety net - it fires purely on "still open
        // after N seconds", not on detecting the specific failure - so it still
        // helps even when the failure is a silent one (e.g. sidecar CSP
        // frame-ancestors rejecting the frame outright, agent-forge#10).
        $actions = $event->getActions();
        $actions[] = $this->launchService->buildLaunchActionButton($launchUrl);
        $event->setActions($actions);
        $event->appendTitleNavContent($this->launchService->buildLaunchHeaderScript());

        return $event;
    }
}
