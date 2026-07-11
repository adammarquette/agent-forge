<?php

/**
 * "Day's Agenda" launch entry point - redirects into the AgentForge sidecar
 * with a main.tab-intent SMART launch, not scoped to any single patient.
 * Loaded via the "Day's Agenda" top-nav tab (see Bootstrap::addAgendaMenuItem()),
 * not the per-patient chart button.
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
use OpenEMR\FHIR\Config\ServerConfig;
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;

// Re-checked server-side even though the menu item is also ACL-gated - a
// menu entry only hides the link, it isn't an access boundary on its own.
if (!AclMain::aclCheckCore('patients', 'appt')) {
    http_response_code(403);
    exit;
}

$config = new AgentForgeGlobalConfig();
$launchService = new AgentForgeLaunchService();

$issuer = $config->getIssuer() ?? (new ServerConfig())->getFhirUrl();
$launchUri = $config->getLaunchUri()
    ?? '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';

$launchUrl = $launchService->buildAgendaLaunchUrl($issuer, $launchUri);
if ($launchUrl === null) {
    http_response_code(500);
    exit('Unable to build the AgentForge agenda launch.');
}

header('Location: ' . $launchUrl);
exit;
