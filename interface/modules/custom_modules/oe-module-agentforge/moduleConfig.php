<?php

/**
 * AgentForge launch configuration - Manage Modules gear-icon settings page.
 *
 * @package   OpenEMR
 * @link      https://www.open-emr.org
 * @author    GauntletAI <support@gauntletai.com>
 * @copyright Copyright (c) 2026 GauntletAI
 * @license   https://github.com/openemr/openemr/blob/master/LICENSE GNU General Public License 3
 */

declare(strict_types=1);

use OpenEMR\Common\Acl\AclMain;
use OpenEMR\Common\Csrf\CsrfUtils;
use OpenEMR\Common\Session\SessionWrapperFactory;
use OpenEMR\Core\Header;
use OpenEMR\Core\ModulesClassLoader;
use OpenEMR\Core\OEGlobalsBag;
use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;

$sessionAllowWrite = true;
require_once dirname(__FILE__, 4) . '/globals.php';

// This page is iframed directly by the Manage Modules "gear icon" (see
// interface/modules/zend_modules/module/Installer/view/installer/installer/configure.phtml),
// bypassing openemr.bootstrap.php's namespace registration if the module
// isn't currently enabled - register it here too so the page still works.
$classLoader = new ModulesClassLoader(OEGlobalsBag::getInstance()->getProjectDir());
$classLoader->registerNamespaceIfNotExists('OpenEMR\\Modules\\AgentForge\\', __DIR__ . DIRECTORY_SEPARATOR . 'src');

if (!AclMain::aclCheckCore('admin', 'manage_modules')) {
    http_response_code(403);
    exit(xlt('Not authorized.'));
}

$session = SessionWrapperFactory::getInstance()->getActiveSession();
$config = new AgentForgeGlobalConfig();
$saved = false;

if (filter_input(INPUT_SERVER, 'REQUEST_METHOD') === 'POST') {
    CsrfUtils::checkCsrfInput(INPUT_POST, dieOnFail: true);
    $launchUri = trim((string) filter_input(INPUT_POST, 'agentforge_launch_uri'));
    $agendaLaunchUri = trim((string) filter_input(INPUT_POST, 'agentforge_agenda_launch_uri'));
    $issuer = trim((string) filter_input(INPUT_POST, 'agentforge_issuer'));
    $launchMode = trim((string) filter_input(INPUT_POST, 'agentforge_launch_mode'));
    // Unchecked checkboxes are simply absent from the POST, so presence = enabled.
    $showAgendaMenu = filter_input(INPUT_POST, 'agentforge_show_agenda_menu') !== null;
    $config->save($launchUri, $agendaLaunchUri, $issuer, $launchMode, $showAgendaMenu);
    $saved = true;
}

// The form only shows/saves the raw override (blank if unset) so that an
// unmodified env-var-derived value never gets silently written back into
// the globals table as a permanent override.
$storedLaunchUri = $config->getStoredLaunchUri();
$storedAgendaLaunchUri = $config->getStoredAgendaLaunchUri();
$storedIssuer = $config->getStoredIssuer();
$effectiveLaunchUri = $config->getLaunchUri() ?? xl('not configured');
$effectiveAgendaLaunchUri = $config->getAgendaLaunchUri() ?? $config->getLaunchUri() ?? xl('not configured');
$effectiveIssuer = $config->getIssuer() ?? xl('not configured');
$launchMode = $config->getLaunchMode();
$showAgendaMenu = $config->isAgendaMenuEnabled();
?>
<!DOCTYPE html>
<html>
<head>
    <title><?php echo xlt('AgentForge Copilot Configuration'); ?></title>
    <?php Header::setupHeader(); ?>
</head>
<body class="p-3">
<h4><?php echo xlt('AgentForge Copilot Launch Configuration'); ?></h4>
<p class="text-muted">
    <?php echo xlt('These settings control the "Launch AgentForge" button shown on the patient chart.'); ?>
    <?php echo xlt('Leave a field blank to fall back to its environment variable'); ?>
    (AGENTFORGE_LAUNCH_URI / AGENTFORGE_AGENDA_LAUNCH_URI / AGENTFORGE_ISSUER).
</p>
<?php if ($saved) : ?>
    <div class="alert alert-success"><?php echo xlt('Settings saved.'); ?></div>
<?php endif; ?>
<form method="post">
    <?php $csrfToken = attr(CsrfUtils::collectCsrfToken(session: $session)); ?>
    <input type="hidden" name="csrf_token_form" value="<?php echo $csrfToken; ?>" />
    <div class="form-group">
        <label for="agentforge_launch_uri"><?php echo xlt('Launch URI'); ?></label>
        <input
            type="text"
            class="form-control"
            id="agentforge_launch_uri"
            name="agentforge_launch_uri"
            value="<?php echo attr($storedLaunchUri); ?>"
            placeholder="<?php echo attr($effectiveLaunchUri); ?>"
        />
        <small class="form-text text-muted">
            <?php echo xlt('AgentForge Copilot\'s single-patient launch-consumption endpoint (the per-patient "Launch AgentForge" button).'); ?>
            <?php echo xlt('Currently effective value:'); ?> <?php echo text($effectiveLaunchUri); ?>
        </small>
    </div>
    <div class="form-group">
        <label for="agentforge_agenda_launch_uri"><?php echo xlt('Agenda Launch URI'); ?></label>
        <input
            type="text"
            class="form-control"
            id="agentforge_agenda_launch_uri"
            name="agentforge_agenda_launch_uri"
            value="<?php echo attr($storedAgendaLaunchUri); ?>"
            placeholder="<?php echo attr($effectiveAgendaLaunchUri); ?>"
        />
        <small class="form-text text-muted">
            <?php echo xlt('The Daily Agenda (roster) launch endpoint - usually the same host as the Launch URI but the .../agenda/launch path. Leave blank to reuse the Launch URI above.'); ?>
            <?php echo xlt('Currently effective value:'); ?> <?php echo text($effectiveAgendaLaunchUri); ?>
        </small>
    </div>
    <div class="form-group">
        <label for="agentforge_issuer"><?php echo xlt('Issuer'); ?></label>
        <input
            type="text"
            class="form-control"
            id="agentforge_issuer"
            name="agentforge_issuer"
            value="<?php echo attr($storedIssuer); ?>"
            placeholder="<?php echo attr($effectiveIssuer); ?>"
        />
        <small class="form-text text-muted">
            <?php echo xlt('The FHIR issuer/audience AgentForge Copilot validates the launch against.'); ?>
            <?php echo xlt('Currently effective value:'); ?> <?php echo text($effectiveIssuer); ?>
        </small>
    </div>
    <div class="form-group">
        <label><?php echo xlt('Launch Mode'); ?></label>
        <div class="form-check">
            <input
                type="radio"
                class="form-check-input"
                id="agentforge_launch_mode_tab"
                name="agentforge_launch_mode"
                value="<?php echo attr(AgentForgeGlobalConfig::LAUNCH_MODE_TAB); ?>"
                <?php echo ($launchMode === AgentForgeGlobalConfig::LAUNCH_MODE_TAB) ? 'checked' : ''; ?>
            />
            <label class="form-check-label" for="agentforge_launch_mode_tab">
                <?php echo xlt('New browser tab (default)'); ?>
            </label>
            <small class="form-text text-muted">
                <?php echo xlt('Opens AgentForge Copilot in a real top-level browser tab. Works regardless of whether AgentForge Copilot is deployed same-site with OpenEMR.'); ?>
            </small>
        </div>
        <div class="form-check">
            <input
                type="radio"
                class="form-check-input"
                id="agentforge_launch_mode_iframe"
                name="agentforge_launch_mode"
                value="<?php echo attr(AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME); ?>"
                <?php echo ($launchMode === AgentForgeGlobalConfig::LAUNCH_MODE_IFRAME) ? 'checked' : ''; ?>
            />
            <label class="form-check-label" for="agentforge_launch_mode_iframe">
                <?php echo xlt('Embedded iframe modal'); ?>
            </label>
            <small class="form-text text-muted">
                <?php echo xlt('Opens AgentForge Copilot inline in a modal on the patient chart. Only use this if AgentForge Copilot is deployed same-site with OpenEMR - otherwise the OAuth login will fail inside the iframe (experimental).'); ?>
            </small>
        </div>
    </div>
    <div class="form-group">
        <label><?php echo xlt('Daily Agenda tab'); ?></label>
        <div class="form-check">
            <input
                type="checkbox"
                class="form-check-input"
                id="agentforge_show_agenda_menu"
                name="agentforge_show_agenda_menu"
                value="1"
                <?php echo $showAgendaMenu ? 'checked' : ''; ?>
            />
            <label class="form-check-label" for="agentforge_show_agenda_menu">
                <?php echo xlt('Show the "Daily Agenda" tab in the top navigation'); ?>
            </label>
            <small class="form-text text-muted">
                <?php echo xlt('When unchecked, the schedule-level Daily Agenda entry point is hidden. The per-patient "Launch AgentForge" button is unaffected.'); ?>
            </small>
        </div>
    </div>
    <button type="submit" class="btn btn-primary"><?php echo xlt('Save'); ?></button>
</form>
</body>
</html>
