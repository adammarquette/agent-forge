<?php

/**
 * SessionUtilEhrLaunchBridgeCookieTest - Tests for the EHR-launch bridge
 * cookie helpers used to recover the core session across a cross-site OAuth
 * redirect (see agent-forge issue #21 / SessionUtil::setEhrLaunchBridgeCookie()).
 *
 * @package   OpenEMR
 * @link      https://www.open-emr.org
 * @license   https://github.com/openemr/openemr/blob/master/LICENSE GNU General Public License 3
 */

declare(strict_types=1);

namespace OpenEMR\Tests\Unit\Common\Session;

use OpenEMR\BC\ServiceContainer;
use OpenEMR\Common\Session\SessionUtil;
use PHPUnit\Framework\TestCase;

class SessionUtilEhrLaunchBridgeCookieTest extends TestCase
{
    /** @var array<mixed, mixed> */
    private array $originalCookie;

    protected function setUp(): void
    {
        parent::setUp();
        $this->originalCookie = $_COOKIE;
    }

    protected function tearDown(): void
    {
        $_COOKIE = $this->originalCookie;
        parent::tearDown();
    }

    public function testGetEhrLaunchBridgeCookieDecryptsAValidCookie(): void
    {
        $coreSessionId = 'test-core-session-id-abc123';
        $encrypted = ServiceContainer::getCrypto()->encryptStandard($coreSessionId);
        $_COOKIE[SessionUtil::EHR_LAUNCH_BRIDGE_COOKIE_NAME] = base64_encode($encrypted);

        $recovered = SessionUtil::getEhrLaunchBridgeCookie();

        self::assertSame($coreSessionId, $recovered);
    }

    public function testGetEhrLaunchBridgeCookieReturnsNullWhenCookieMissing(): void
    {
        unset($_COOKIE[SessionUtil::EHR_LAUNCH_BRIDGE_COOKIE_NAME]);

        self::assertNull(SessionUtil::getEhrLaunchBridgeCookie());
    }

    public function testGetEhrLaunchBridgeCookieReturnsNullWhenCookieEmpty(): void
    {
        $_COOKIE[SessionUtil::EHR_LAUNCH_BRIDGE_COOKIE_NAME] = '';

        self::assertNull(SessionUtil::getEhrLaunchBridgeCookie());
    }

    public function testGetEhrLaunchBridgeCookieFailsClosedOnInvalidBase64(): void
    {
        // base64_decode(..., true) rejects characters outside the base64 alphabet
        $_COOKIE[SessionUtil::EHR_LAUNCH_BRIDGE_COOKIE_NAME] = 'not valid base64!!! ###';

        self::assertNull(SessionUtil::getEhrLaunchBridgeCookie());
    }

    public function testGetEhrLaunchBridgeCookieFailsClosedOnTamperedCiphertext(): void
    {
        $coreSessionId = 'test-core-session-id-abc123';
        $encrypted = ServiceContainer::getCrypto()->encryptStandard($coreSessionId);
        // Flip a byte in the middle of the ciphertext - the authenticated
        // cipher (AES-256-CBC+HMAC) must reject this, not silently decrypt
        // to garbage or throw uncaught.
        $tampered = substr($encrypted, 0, 10) . chr(ord($encrypted[10]) ^ 0xFF) . substr($encrypted, 11);
        $_COOKIE[SessionUtil::EHR_LAUNCH_BRIDGE_COOKIE_NAME] = base64_encode($tampered);

        self::assertNull(SessionUtil::getEhrLaunchBridgeCookie());
    }

    public function testGetEhrLaunchBridgeCookieFailsClosedOnValidBase64ButNotEncrypted(): void
    {
        $_COOKIE[SessionUtil::EHR_LAUNCH_BRIDGE_COOKIE_NAME] = base64_encode('plain-text-not-encrypted');

        self::assertNull(SessionUtil::getEhrLaunchBridgeCookie());
    }

    public function testSetEhrLaunchBridgeCookieDoesNotThrow(): void
    {
        // setcookie() in CLI SAPI queues a header without a real client to
        // send it to - this is a smoke test that the encrypt+setcookie path
        // completes without error. The round trip itself is covered above
        // via a manually-constructed $_COOKIE, since setcookie() does not
        // update $_COOKIE within the same process.
        SessionUtil::setEhrLaunchBridgeCookie('test-core-session-id-abc123');
        $this->addToAssertionCount(1);
    }

    public function testClearEhrLaunchBridgeCookieDoesNotThrow(): void
    {
        SessionUtil::clearEhrLaunchBridgeCookie();
        $this->addToAssertionCount(1);
    }
}
