<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge;

use OpenEMR\Common\Acl\AclMain;
use OpenEMR\Common\Session\SessionWrapperFactory;
use OpenEMR\Events\UserInterface\PageHeadingRenderEvent;
use OpenEMR\FHIR\Config\ServerConfig;
use OpenEMR\FHIR\SMART\SMARTLaunchToken;
use OpenEMR\Menu\MenuEvent;
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

    /**
     * Unique top-nav tab id for the "Day's Agenda" menu item. OpenEMR's tab
     * framework (interface/main/tabs/js/tabs_view_model.js's navigateTab()/
     * activateTabByName()) opens a new tab keyed by this string the first
     * time it's clicked, rather than reusing an existing frame - "pop" is
     * reserved for a popup dialog, so this must be a fresh id.
     */
    private const AGENDA_TAB_TARGET = 'agf';

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
        $this->eventDispatcher->addListener(
            MenuEvent::MENU_UPDATE,
            $this->addAgendaMenuItem(...)
        );
    }

    /**
     * Adds a top-level "Day's Agenda" tab, right after Calendar, giving a
     * provider a schedule-level entry point into AgentForge instead of the
     * per-patient chart button. Unlike renderLaunchButton() this isn't scoped
     * to any single patient - the sidecar resolves the day's roster itself
     * from the launching clinician's own FHIR-scoped identity.
     */
    public function addAgendaMenuItem(MenuEvent $event): MenuEvent
    {
        $menuItem = new \stdClass();
        $menuItem->requirement = 0;
        $menuItem->target = self::AGENDA_TAB_TARGET;
        $menuItem->menu_id = 'agf0';
        // Avoids an apostrophe in the label: OpenEMR's client-side menu
        // renderer substitutes a backtick for straight apostrophes in
        // labels ("Day's Agenda" -> "Day`s Agenda"), so this sidesteps that
        // rather than shipping a visibly garbled nav label.
        $menuItem->label = xlt('Daily Agenda');
        $menuItem->url = '/interface/modules/custom_modules/oe-module-agentforge/public/agenda-launch.php';
        $menuItem->children = [];
        $menuItem->acl_req = ['patients', 'appt'];
        $menuItem->global_req = [];

        $menu = $event->getMenu();
        $calendarIndex = null;
        foreach ($menu as $index => $item) {
            if (!($item instanceof \stdClass) || !is_int($index)) {
                continue;
            }
            if (($item->menu_id ?? null) === 'cal0') {
                $calendarIndex = $index;
                break;
            }
        }

        if ($calendarIndex === null) {
            $menu[] = $menuItem;
        } else {
            array_splice($menu, $calendarIndex + 1, 0, [$menuItem]);
        }

        $event->setMenu($menu);

        return $event;
    }

    /**
     * Renders the button. In tab mode (default), points it at
     * public/patient-launch.php rather than building the sidecar launch URL
     * (or setting the EHR-launch bridge cookie) here directly - both of
     * those need a clean HTTP response with no prior output, and
     * setcookie() silently fails once headers are sent, which is exactly
     * what's already happened by the time a PageHeadingRenderEvent listener
     * runs this deep into page rendering. patient-launch.php runs as its
     * own fresh request instead (see its doc comment). In iframe mode, the
     * bridge cookie is irrelevant (dlgopen() relies on the sidecar being
     * same-site instead - see IFRAME_REVERT.md), so the sidecar launch URL
     * is built directly, inline, same as before agent-forge#21's fix.
     */
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

        $launchMode = $this->config->getLaunchMode();

        if ($launchMode === AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME) {
            $launchUrl = $this->buildDirectPatientLaunchUrl((string) $pid);
            if ($launchUrl === null) {
                return $event;
            }
            $windowName = null;
        } else {
            // Real top-level tab via window.open() instead of dlgopen()'s
            // modal iframe: the EHR-launch OAuth round-trip relies on a
            // SameSite=Lax bridge cookie (set by patient-launch.php) to
            // recover the session, and Lax's cross-site exception only
            // applies to top-level navigations, not iframes. One tab per
            // patient: the window name is scoped by pid so switching
            // patients opens a new tab rather than replacing an
            // already-open conversation, and re-launching the same patient
            // refocuses their existing tab.
            $launchUrl = '/interface/modules/custom_modules/oe-module-agentforge/public/patient-launch.php';
            $windowName = 'agentforge-launch-' . $pid;
        }

        $actions = $event->getActions();
        $actions[] = $this->launchService->buildLaunchActionButton($launchUrl, $launchMode, $windowName);
        $event->setActions($actions);
        $event->appendTitleNavContent($this->launchService->buildLaunchHeaderScript());

        return $event;
    }

    /**
     * Builds the sidecar launch URL directly (iframe mode only) - mirrors
     * public/patient-launch.php's token-building, duplicated here rather
     * than shared because this path has no bridge cookie to set and must
     * stay pure page-render logic (no redirect, no separate request).
     */
    private function buildDirectPatientLaunchUrl(string $pid): ?string
    {
        $launchToken = new SMARTLaunchToken();
        $launchToken->setPatient($pid);
        $launchToken->setIntent(SMARTLaunchToken::INTENT_PATIENT_DEMOGRAPHICS_DIALOG);

        $serializedToken = $launchToken->serialize();
        if (!is_string($serializedToken)) {
            return null;
        }

        $issuer = $this->config->getIssuer() ?? (new ServerConfig())->getFhirUrl();
        $launchUri = $this->config->getLaunchUri()
            ?? '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';

        return $this->launchService->buildLaunchUrl($serializedToken, $issuer, $launchUri, $pid);
    }
}
