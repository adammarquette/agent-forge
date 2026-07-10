<?php

declare(strict_types=1);

namespace OpenEMR\Tests\Unit\Modules\AgentForge;

use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;
use PHPUnit\Framework\TestCase;

class AgentForgeLaunchServiceTest extends TestCase
{
    public function testBuildLaunchUrlUsesLaunchAndIssuerParameters(): void
    {
        require_once __DIR__ . '/../../../../../interface/modules/custom_modules/oe-module-agentforge/src/Launch/AgentForgeLaunchService.php';

        $service = new AgentForgeLaunchService();

        $launchUrl = $service->buildLaunchUrl(
            'test-launch-token',
            'https://openemr.example.com/fhir',
            'https://copilot.example.com/launch'
        );

        self::assertSame(
            'https://copilot.example.com/launch?launch=test-launch-token&iss=https%3A%2F%2Fopenemr.example.com%2Ffhir&aud=https%3A%2F%2Fopenemr.example.com%2Ffhir',
            $launchUrl
        );
    }

    public function testBuildLaunchUrlAddsPatientContextWhenProvided(): void
    {
        require_once __DIR__ . '/../../../../../interface/modules/custom_modules/oe-module-agentforge/src/Launch/AgentForgeLaunchService.php';

        $service = new AgentForgeLaunchService();

        $launchUrl = $service->buildLaunchUrl(
            'test-launch-token',
            'https://openemr.example.com/fhir',
            'https://copilot.example.com/launch',
            'patient-123'
        );

        self::assertStringContainsString('patient=patient-123', $launchUrl);
    }
}
