<?php

declare(strict_types=1);

// Background Service entry point for AgentForge document ingestion (agent-forge#44). OpenEMR's service
// runner require_once's this file and calls agentforge_ingest_new_documents() on each interval. Registered
// via agentforge_register_ingest_service() below - call it from the module's enable/setup path (mirrors
// oe-module-claimrev-connect/src/ClaimRevModuleSetup.php).

use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Ingest\DocumentIngestService;
use OpenEMR\Services\Background\BackgroundServiceDefinition;
use OpenEMR\Services\Background\BackgroundServiceRegistry;

if (!function_exists('agentforge_ingest_new_documents')) {
    /**
     * The scan invoked each interval by the Background Service runner. Kept thin - the work is in
     * DocumentIngestService so it stays unit-reviewable and the entry point is just wiring.
     */
    function agentforge_ingest_new_documents(): void
    {
        (new DocumentIngestService(new AgentForgeGlobalConfig()))->run();
    }
}

if (!function_exists('agentforge_register_ingest_service')) {
    /**
     * Registers the ingestion cron with OpenEMR's Background Services. Idempotent via the Registry, so a
     * module upgrade will not reset an admin's enable/disable toggle. Call once from the module enable path.
     */
    function agentforge_register_ingest_service(): void
    {
        (new BackgroundServiceRegistry())->register(new BackgroundServiceDefinition(
            name: 'AgentForge_Ingest',
            title: 'AgentForge document ingestion',
            function: 'agentforge_ingest_new_documents',
            requireOnce: '/interface/modules/custom_modules/oe-module-agentforge/src/ingest_service.php',
            executeInterval: 2,
            sortOrder: 100,
            active: true,
        ));
    }
}
