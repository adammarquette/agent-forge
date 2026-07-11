<?php

/**
 * Register class.
 *
 * @package   OpenEMR
 * @link      https://www.open-emr.org
 * @author    Brady Miller <brady.g.miller@gmail.com>
 * @copyright Copyright (c) 2022 Brady Miller <brady.g.miller@gmail.com>
 * @license   https://github.com/openemr/openemr/blob/master/LICENSE GNU General Public License 3
 */

namespace OpenEMR\Common\Command;

use Installer\Model\InstModuleTable;
use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Core\OEGlobalsBag;
use Symfony\Component\Console\Command\Command;
use Symfony\Component\Console\Input\InputDefinition;
use Symfony\Component\Console\Input\InputInterface;
use Symfony\Component\Console\Input\InputOption;
use Symfony\Component\Console\Output\OutputInterface;

class Register extends Command
{
    protected function configure(): void
    {
        $this
            ->setName('openemr:register')
            ->setDescription('Register (and for custom modules, install + enable) a zend or custom module')
            ->addUsage('--site=default --mtype=zend --modname=Carecoordination')
            ->addUsage('--site=default --mtype=custom --modname=oe-module-agentforge')
            ->setDefinition(
                new InputDefinition([
                    new InputOption('mtype', null, InputOption::VALUE_REQUIRED, '"zend" or "custom"'),
                    new InputOption('modname', null, InputOption::VALUE_REQUIRED, 'Module directory name'),
                    new InputOption('site', null, InputOption::VALUE_REQUIRED, 'Name of site', 'default'),
                ])
            )
        ;
    }

    protected function execute(InputInterface $input, OutputInterface $output): int
    {
        $mtype = $input->getOption('mtype');
        if (!is_string($mtype) || $mtype === '') {
            $output->writeln('mtype parameter is missing (required), so exiting');
            return 2;
        }
        $moduleName = $input->getOption('modname');
        if (!is_string($moduleName) || $moduleName === '') {
            $output->writeln('modname parameter is missing (required), so exiting');
            return 2;
        }

        if ($mtype === 'custom') {
            return $this->registerCustomModule($moduleName, $output);
        }
        if ($mtype !== 'zend') {
            $output->writeln('mtype parameter that is not "zend" or "custom" is not supported');
            return 1;
        }

        $rel_path = "public/" . $moduleName . "/";
        $zendModDir = OEGlobalsBag::getInstance()->getString('zendModDir');
        $modulesApplication = OEGlobalsBag::getInstance()->getModulesApplication();
        $table = $modulesApplication->getServiceManager()->build(InstModuleTable::class);

        if ($table->register($moduleName, $rel_path, 0, $zendModDir)) {
            $output->writeln('Success');
            return 0;
        } else {
            $output->writeln('Failure');
            return 1;
        }
    }

    /**
     * Registers, installs, and enables a custom module (interface/modules/custom_modules/<modname>)
     * in one step. Idempotent: safe to run on every container boot - InstModuleTable::register()
     * no-ops on a directory that's already registered, and re-asserting mod_active=1 on an already
     * enabled module is harmless.
     */
    private function registerCustomModule(string $moduleName, OutputInterface $output): int
    {
        $modulesApplication = OEGlobalsBag::getInstance()->getModulesApplication();
        $table = $modulesApplication->getServiceManager()->build(InstModuleTable::class);

        $modId = $table->register($moduleName, $moduleName . "/index.php");
        if ($modId === false) {
            $modId = QueryUtils::fetchSingleValue(
                "SELECT mod_id FROM modules WHERE mod_directory = ?",
                'mod_id',
                [$moduleName]
            );
        }
        if (!is_int($modId) && !(is_string($modId) && is_numeric($modId))) {
            $output->writeln('Failure: could not register or locate module ' . $moduleName);
            return 1;
        }
        $modId = (int) $modId;

        $fullDirectory = OEGlobalsBag::getInstance()->getSrcDir() . "/../"
            . OEGlobalsBag::getInstance()->getString('baseModDir')
            . OEGlobalsBag::getInstance()->getString('customModDir')
            . "/" . $moduleName;
        if (!$table->installSQL($modId, InstModuleTable::MODULE_TYPE_CUSTOM, $fullDirectory)) {
            $output->writeln('Failure: could not install module ' . $moduleName);
            return 1;
        }
        $table->updateRegistered($modId, '', ['', '', '']);
        $table->updateRegistered($modId, 'mod_active=1');

        $output->writeln('Success');
        return 0;
    }
}
