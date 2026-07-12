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
    $issuer = trim((string) filter_input(INPUT_POST, 'agentforge_issuer'));
    $launchMode = trim((string) filter_input(INPUT_POST, 'agentforge_launch_mode'));
    $config->save($launchUri, $issuer, $launchMode);
    $saved = true;
}

// The form only shows/saves the raw override (blank if unset) so that an
// unmodified env-var-derived value never gets silently written back into
// the globals table as a permanent override.
$storedLaunchUri = $config->getStoredLaunchUri();
$storedIssuer = $config->getStoredIssuer();
$effectiveLaunchUri = $config->getLaunchUri() ?? xl('not configured');
$effectiveIssuer = $config->getIssuer() ?? xl('not configured');
$launchMode = $config->getLaunchMode();
?>
<!DOCTYPE html>
<html>
<head>
    <title><?php echo xlt('AgentForge Configuration'); ?></title>
    <?php Header::setupHeader(); ?>
</head>
<body class="p-3">
<h4><?php echo xlt('AgentForge Launch Configuration'); ?></h4>
<p class="text-muted">
    <?php echo xlt('These settings control the "Launch AgentForge" button shown on the patient chart.'); ?>
    <?php echo xlt('Leave a field blank to fall back to its environment variable'); ?>
    (AGENTFORGE_LAUNCH_URI / AGENTFORGE_ISSUER).
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
            <?php echo xlt('The AgentForge sidecar\'s launch-consumption endpoint.'); ?>
            <?php echo xlt('Currently effective value:'); ?> <?php echo text($effectiveLaunchUri); ?>
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
            <?php echo xlt('The FHIR issuer/audience the sidecar validates the launch against.'); ?>
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
                <?php echo xlt('Opens the sidecar in a real top-level browser tab. Works regardless of whether the sidecar is deployed same-site with OpenEMR.'); ?>
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
                <?php echo xlt('Opens the sidecar inline in a modal on the patient chart. Only use this if the sidecar is deployed same-site with OpenEMR - otherwise the OAuth login will fail inside the iframe (agent-forge#21).'); ?>
            </small>
        </div>
    </div>
    <button type="submit" class="btn btn-primary"><?php echo xlt('Save'); ?></button>
</form>
</body>
</html>
