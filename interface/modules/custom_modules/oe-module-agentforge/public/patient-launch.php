<?php

/**
 * Per-patient "Launch AgentForge" entry point - opened via window.open() as
 * a real top-level browser tab (see Bootstrap::renderLaunchButton()), not
 * loaded mid-page. This must run as a fresh HTTP request with no prior
 * output, since setting the EHR-launch bridge cookie
 * (SessionUtil::setEhrLaunchBridgeCookie()) via setcookie() silently fails
 * once headers have already been sent - which is exactly what happens if
 * the cookie is set from deep inside PageHeadingRenderEvent's page-render
 * pipeline instead of a dedicated entry point like this one.
 *
 * @package   OpenEMR
 * @link      https://www.open-emr.org
 * @author    Adam Marquette
 * @copyright Copyright (c) 2026 Adam Marquette
 * @license   https://github.com/openemr/openemr/blob/master/LICENSE GNU General Public License 3
 */

declare(strict_types=1);

require_once __DIR__ . "/../../../../globals.php";

use OpenEMR\Common\Acl\AclMain;
use OpenEMR\Common\Session\SessionUtil;
use OpenEMR\Common\Session\SessionWrapperFactory;
use OpenEMR\FHIR\Config\ServerConfig;
use OpenEMR\FHIR\SMART\SMARTLaunchToken;
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;

// Re-checked server-side even though the button is also ACL-gated - a
// hidden button is a UI convenience, not an access boundary on its own.
if (!AclMain::aclCheckCore('patients', 'demo')) {
    http_response_code(403);
    exit;
}

$session = SessionWrapperFactory::getInstance()->getActiveSession();

// Deliberately not a query param: the patient context comes from the
// session's own current pid (set when the demographics page was loaded),
// not a client-suppliable value - this endpoint must not become an
// arbitrary cross-patient launch oracle.
$pid = $session->get('pid');
if (!is_numeric($pid) || (int) $pid <= 0) {
    http_response_code(400);
    exit('No active patient context.');
}

$launchToken = new SMARTLaunchToken();
$launchToken->setPatient((string) $pid);
$launchToken->setIntent(SMARTLaunchToken::INTENT_PATIENT_DEMOGRAPHICS_DIALOG);

$serializedToken = $launchToken->serialize();
if (!is_string($serializedToken)) {
    http_response_code(500);
    exit('Unable to build the AgentForge launch.');
}

$config = new AgentForgeGlobalConfig();
$launchService = new AgentForgeLaunchService();

$issuer = $config->getIssuer() ?? (new ServerConfig())->getFhirUrl();
$launchUri = $config->getLaunchUri()
    ?? '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';
$launchUrl = $launchService->buildLaunchUrl($serializedToken, $issuer, $launchUri, (string) $pid);

// Set fresh at launch time (not login time, and not mid-page-render) - see
// SessionUtil::setEhrLaunchBridgeCookie()'s doc comment for why.
SessionUtil::setEhrLaunchBridgeCookie($session->getId());

header('Location: ' . $launchUrl);
exit;
