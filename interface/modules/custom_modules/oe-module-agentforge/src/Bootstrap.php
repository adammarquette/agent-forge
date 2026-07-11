<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge;

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
        $this->eventDispatcher->addListener(RenderEvent::EVENT_SECTION_LIST_RENDER_AFTER, $this->renderLaunchButton(...));
    }

    public function renderLaunchButton(RenderEvent $event): void
    {
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
        $launchUri = getenv('AGENTFORGE_LAUNCH_URI') ?: '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';
        $launchUrl = $this->launchService->buildLaunchUrl($serializedToken, $issuer, $launchUri, (string) $pid);

        echo '<section class="card mb-2">';
        echo '<div class="p-2">';
        echo '<a class="btn btn-sm btn-primary" href="' . attr($launchUrl) . '">' . xlt('Launch AgentForge') . '</a>';
        echo '</div>';
        echo '</section>';
    }
}
