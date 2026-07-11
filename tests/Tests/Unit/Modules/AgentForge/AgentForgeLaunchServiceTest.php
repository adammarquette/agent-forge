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

    public function testBuildLaunchActionButtonCarriesLaunchUrlAsDataAttribute(): void
    {
        $service = new AgentForgeLaunchService();

        $launchUrl = 'https://copilot.example.com/launch'
            . '?launch=test-token&iss=https%3A%2F%2Fopenemr.example.com%2Ffhir';
        $button = $service->buildLaunchActionButton($launchUrl);

        self::assertSame('agentforge-launch-btn', $button->getID());
        self::assertSame('agentforgeHeaderLaunch', $button->getClickHandlerFunctionName());
        self::assertSame(['data-launch-url' => $launchUrl], $button->getAttributes());
    }

    public function testBuildLaunchHeaderScriptOpensSidecarInModalIframe(): void
    {
        $service = new AgentForgeLaunchService();

        $script = $service->buildLaunchHeaderScript();

        self::assertStringContainsString('function agentforgeHeaderLaunch()', $script);
        self::assertStringContainsString('dlgopen(', $script);
        self::assertStringContainsString('allowExternal: true', $script);
        self::assertStringContainsString(
            'getAttribute("data-launch-url")',
            $script,
            'launch URL must be read off the button element, not a plain full-page redirect link'
        );
    }

    public function testBuildLaunchHeaderScriptIncludesLoadFailureFallback(): void
    {
        $service = new AgentForgeLaunchService();

        $script = $service->buildLaunchHeaderScript();

        self::assertStringContainsString('id="agentforge-launch-warning"', $script);
        self::assertStringContainsString('display:none', $script, 'warning must start hidden');
        self::assertStringContainsString('8000', $script, 'must arm the load-failure timeout');
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

    public function testBuildAgendaLaunchUrlCarriesNoPatientContext(): void
    {
        $service = new AgentForgeLaunchService();

        $launchUrl = $service->buildAgendaLaunchUrl(
            'https://openemr.example.com/fhir',
            'https://copilot.example.com/launch'
        );

        self::assertNotNull($launchUrl);
        self::assertStringStartsWith('https://copilot.example.com/launch?launch=', $launchUrl);
        self::assertStringNotContainsString(
            'patient=',
            $launchUrl,
            'the day\'s-agenda launch must not scope to a single patient - the sidecar resolves the roster itself'
        );
    }
}
