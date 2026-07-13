<?php

/**
 * "Day's Agenda" launch entry point - a main.tab-intent SMART launch, not
 * scoped to any single patient. Loaded via the "Day's Agenda" top-nav tab
 * (see Bootstrap::addAgendaMenuItem()), which loads this page inside
 * OpenEMR's own iframe-based tab framework - not the per-patient chart
 * button.
 *
 * In tab mode (default), this page must NOT redirect that iframe
 * cross-origin itself. The EHR-launch OAuth round-trip relies on a
 * SameSite=Lax bridge cookie (SessionUtil::setEhrLaunchBridgeCookie()) to
 * recover the session across the sidecar's redirect back to OpenEMR, and
 * Lax's cross-site exception only covers top-level navigations, not
 * iframes - the same reason a plain server-side redirect here would land
 * right back in the login-page bug this replaces. Instead, it opens a real
 * top-level browser tab via window.open().
 *
 * In iframe mode, the bridge cookie is irrelevant (the sidecar is assumed
 * same-site with OpenEMR - see Documentation/agent-forge/IFRAME_REVERT.md),
 * so this just does a plain redirect and lets OpenEMR's own tab iframe
 * navigate directly to the sidecar, same as before agent-forge#21's fix.
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
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Launch\AgentForgeLaunchService;

// Re-checked server-side even though the menu item is also ACL-gated - a
// menu entry only hides the link, it isn't an access boundary on its own.
if (!AclMain::aclCheckCore('patients', 'appt')) {
    http_response_code(403);
    exit;
}

$session = SessionWrapperFactory::getInstance()->getActiveSession();

$config = new AgentForgeGlobalConfig();
$launchService = new AgentForgeLaunchService();

$issuer = $config->getIssuer() ?? (new ServerConfig())->getFhirUrl();
// Roster launch has its own endpoint (.../agentforge/agenda/launch). Fall back to the shared
// single-patient launch URI when unset so an unconfigured install behaves as it did before.
$launchUri = $config->getAgendaLaunchUri()
    ?? $config->getLaunchUri()
    ?? '/interface/modules/custom_modules/oe-module-agentforge/public/launch.php';

$launchUrl = $launchService->buildAgendaLaunchUrl($issuer, $launchUri);
if ($launchUrl === null) {
    http_response_code(500);
    exit('Unable to build the AgentForge agenda launch.');
}

if ($config->getLaunchMode() === AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME) {
    header('Location: ' . $launchUrl);
    exit;
}

// Set fresh at launch time (not login time) - see
// SessionUtil::setEhrLaunchBridgeCookie()'s doc comment for why.
SessionUtil::setEhrLaunchBridgeCookie($session->getId());

$jsLaunchUrl = js_escape($launchUrl);
$openingText = xlt('Opening AgentForge in a new tab...');
$warningText = xlt('AgentForge could not be opened. Please allow pop-ups for this site and try again.');
?>
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title><?php echo xlt('Daily Agenda'); ?></title>
</head>
<body>
<p><?php echo $openingText; ?></p>
<p id="agentforge-agenda-warning" style="display:none;color:#a94442;"><?php echo $warningText; ?></p>
<script>
var win = window.open(<?php echo $jsLaunchUrl; ?>, "agentforge-agenda");
if (!win || win.closed || typeof win.closed === "undefined") {
    document.getElementById("agentforge-agenda-warning").style.display = "block";
}
</script>
</body>
</html>
<?php
exit;
