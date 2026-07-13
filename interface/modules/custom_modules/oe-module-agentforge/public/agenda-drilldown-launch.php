<?php

/**
 * Agenda drill-down launch - a PATIENT-scoped launch for a patient chosen from the
 * Day's Agenda roster (passed as a FHIR uuid), rather than the session's current chart.
 *
 * The roster itself launches user-scoped (it spans patients). But drilling into one patient
 * needs a PATIENT-scoped token: the per-patient brief reads Encounter / DiagnosticReport /
 * DocumentReference, and OpenEMR gates those under a *user* scope behind a hard
 * `encounters/auth_a` ACL (FhirEncounter route's non-patient branch), which a normal provider
 * lacks - so a user-scoped drill-down 403s on them. A patient-scoped token takes the
 * `isPatientRequest()` patient-binding path and reads them fine (the same path the per-patient
 * "Launch AgentForge" button already uses). reference: gitlab#76
 *
 * The provider already has chart access to any patient (patients/demo - the same ACL the patient
 * finder uses), so launching the selected roster patient is not an access escalation; it just
 * scopes the token to that one patient. The uuid is validated for format and existence so this
 * endpoint can't be driven with an arbitrary string.
 *
 * Must run as a fresh HTTP request with no prior output - the EHR-launch bridge cookie
 * (SessionUtil::setEhrLaunchBridgeCookie()) silently fails once headers are sent, same as
 * patient-launch.php.
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
use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Common\Session\SessionUtil;
use OpenEMR\Common\Session\SessionWrapperFactory;
use OpenEMR\Common\Uuid\UuidRegistry;
use OpenEMR\FHIR\Config\ServerConfig;
use OpenEMR\FHIR\SMART\SMARTLaunchToken;
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;

if (!AclMain::aclCheckCore('patients', 'demo')) {
    http_response_code(403);
    exit;
}

$session = SessionWrapperFactory::getInstance()->getActiveSession();

// The roster row's patient uuid (the FHIR/SMART patient id). Validate the format, then that the
// patient exists, so this can't be driven with an arbitrary/garbage value.
$patientUuid = strtolower(trim((string) filter_input(INPUT_GET, 'patient')));
if (preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/', $patientUuid) !== 1) {
    http_response_code(400);
    exit('Invalid patient.');
}
$patientRow = QueryUtils::querySingleRow('SELECT pid FROM patient_data WHERE uuid = ?', [UuidRegistry::uuidToBytes($patientUuid)]);
if (!is_array($patientRow) || !isset($patientRow['pid'])) {
    http_response_code(404);
    exit('Patient not found.');
}

$launchToken = new SMARTLaunchToken();
$launchToken->setPatient($patientUuid);
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
$launchUrl = $launchService->buildLaunchUrl($serializedToken, $issuer, $launchUri, $patientUuid);

// Set fresh at launch time (not login/mid-render) - see SessionUtil::setEhrLaunchBridgeCookie().
SessionUtil::setEhrLaunchBridgeCookie($session->getId());

header('Location: ' . $launchUrl);
exit;
