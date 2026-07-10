<?php

declare(strict_types=1);

use OpenEMR\Core\ModulesClassLoader;
use OpenEMR\Core\OEGlobalsBag;

$file = OEGlobalsBag::getInstance()->getProjectDir();
$classLoader = new ModulesClassLoader($file);
$classLoader->registerNamespaceIfNotExists('OpenEMR\\Modules\\AgentForge\\', __DIR__ . DIRECTORY_SEPARATOR . 'src');
