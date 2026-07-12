<?php

declare(strict_types=1);

namespace OpenEMR\Tests\Unit\Modules\AgentForge;

use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use PHPUnit\Framework\TestCase;

class AgentForgeGlobalConfigTest extends TestCase
{
    protected function setUp(): void
    {
        $GLOBALS['disable_translation'] = true;
        require_once __DIR__ . '/../../../../../interface/modules/custom_modules/'
            . 'oe-module-agentforge/src/Config/AgentForgeGlobalConfig.php';
    }

    protected function tearDown(): void
    {
        unset(
            $GLOBALS['disable_translation'],
            $GLOBALS[AgentForgeGlobalConfig::LAUNCH_URI],
            $GLOBALS[AgentForgeGlobalConfig::ISSUER],
            $GLOBALS[AgentForgeGlobalConfig::LAUNCH_MODE],
        );
        putenv('AGENTFORGE_LAUNCH_URI');
        putenv('AGENTFORGE_ISSUER');
    }

    public function testGetLaunchUriReturnsNullWhenNothingConfigured(): void
    {
        $config = new AgentForgeGlobalConfig();

        self::assertNull($config->getLaunchUri());
    }

    public function testGetLaunchUriFallsBackToEnvVarWhenGlobalUnset(): void
    {
        putenv('AGENTFORGE_LAUNCH_URI=https://env.example.com/launch');

        $config = new AgentForgeGlobalConfig();

        self::assertSame('https://env.example.com/launch', $config->getLaunchUri());
    }

    public function testGetLaunchUriPrefersStoredGlobalOverEnvVar(): void
    {
        putenv('AGENTFORGE_LAUNCH_URI=https://env.example.com/launch');
        $GLOBALS[AgentForgeGlobalConfig::LAUNCH_URI] = 'https://db.example.com/launch';

        $config = new AgentForgeGlobalConfig();

        self::assertSame('https://db.example.com/launch', $config->getLaunchUri());
    }

    public function testGetIssuerPrefersStoredGlobalOverEnvVar(): void
    {
        putenv('AGENTFORGE_ISSUER=https://env.example.com/fhir');
        $GLOBALS[AgentForgeGlobalConfig::ISSUER] = 'https://db.example.com/fhir';

        $config = new AgentForgeGlobalConfig();

        self::assertSame('https://db.example.com/fhir', $config->getIssuer());
    }

    public function testGetStoredLaunchUriIgnoresEnvVarFallback(): void
    {
        putenv('AGENTFORGE_LAUNCH_URI=https://env.example.com/launch');

        $config = new AgentForgeGlobalConfig();

        self::assertSame('', $config->getStoredLaunchUri());
    }

    public function testGetStoredIssuerReturnsSavedGlobalValue(): void
    {
        $GLOBALS[AgentForgeGlobalConfig::ISSUER] = 'https://db.example.com/fhir';

        $config = new AgentForgeGlobalConfig();

        self::assertSame('https://db.example.com/fhir', $config->getStoredIssuer());
    }

    public function testGetLaunchModeDefaultsToTabWhenUnset(): void
    {
        $config = new AgentForgeGlobalConfig();

        self::assertSame(AgentForgeGlobalConfig::LAUNCH_MODE_TAB, $config->getLaunchMode());
    }

    public function testGetLaunchModeReturnsStoredIframeValue(): void
    {
        $GLOBALS[AgentForgeGlobalConfig::LAUNCH_MODE] = AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME;

        $config = new AgentForgeGlobalConfig();

        self::assertSame(AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME, $config->getLaunchMode());
    }

    public function testGetLaunchModeFailsClosedToTabOnUnrecognizedValue(): void
    {
        $GLOBALS[AgentForgeGlobalConfig::LAUNCH_MODE] = 'some-unrecognized-value';

        $config = new AgentForgeGlobalConfig();

        self::assertSame(AgentForgeGlobalConfig::LAUNCH_MODE_TAB, $config->getLaunchMode());
    }
}
