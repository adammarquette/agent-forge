<?php

declare(strict_types=1);

namespace OpenEMR\Tests\Unit\Modules\AgentForge;

use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;
use PHPUnit\Framework\TestCase;

class AgentForgeLaunchServiceTest extends TestCase
{
    protected function setUp(): void
    {
        $GLOBALS['disable_translation'] = true;
        require_once __DIR__ . '/../../../../../interface/modules/custom_modules/'
            . 'oe-module-agentforge/src/Launch/AgentForgeLaunchService.php';
    }

    protected function tearDown(): void
    {
        unset($GLOBALS['disable_translation']);
    }

    public function testBuildLaunchButtonMarkupOpensSidecarInModalIframe(): void
    {
        $service = new AgentForgeLaunchService();

        $launchUrl = 'https://copilot.example.com/launch'
            . '?launch=test-token&iss=https%3A%2F%2Fopenemr.example.com%2Ffhir';
        $markup = $service->buildLaunchButtonMarkup($launchUrl);

        self::assertStringContainsString('id="agentforge-launch-btn"', $markup);
        self::assertStringContainsString('dlgopen(', $markup);
        self::assertStringContainsString('allowExternal: true', $markup);
        self::assertStringContainsString('https:\/\/copilot.example.com\/launch', $markup);
        self::assertStringNotContainsString(
            '<a ',
            $markup,
            'launch trigger must not be a plain full-page redirect link'
        );
    }

    public function testBuildLaunchButtonMarkupIncludesLoadFailureFallback(): void
    {
        $service = new AgentForgeLaunchService();

        $markup = $service->buildLaunchButtonMarkup('https://copilot.example.com/launch');

        self::assertStringContainsString('id="agentforge-launch-warning"', $markup);
        self::assertStringContainsString('display:none', $markup, 'warning must start hidden');
        self::assertStringContainsString('8000', $markup, 'must arm the load-failure timeout');
    }

    public function testBuildLaunchUrlUsesLaunchAndIssuerParameters(): void
    {
        $service = new AgentForgeLaunchService();

        $launchUrl = $service->buildLaunchUrl(
            'test-launch-token',
            'https://openemr.example.com/fhir',
            'https://copilot.example.com/launch'
        );

        $expectedUrl = 'https://copilot.example.com/launch'
            . '?launch=test-launch-token'
            . '&iss=https%3A%2F%2Fopenemr.example.com%2Ffhir'
            . '&aud=https%3A%2F%2Fopenemr.example.com%2Ffhir';
        self::assertSame($expectedUrl, $launchUrl);
    }

    public function testBuildLaunchUrlAddsPatientContextWhenProvided(): void
    {
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
