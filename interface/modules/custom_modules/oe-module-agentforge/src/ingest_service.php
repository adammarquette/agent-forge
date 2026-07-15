<?php

declare(strict_types=1);

// Background Service entry point for AgentForge document ingestion (agent-forge#44). OpenEMR's service
// runner require_once's this file and calls agentforge_ingest_new_documents() by name each interval - which
// is why this must be a global function (the runner does function_exists() + $function()), not a class
// method. Registration lives in Bootstrap::registerBackgroundServices(), called from openemr.bootstrap.php.

use OpenEMR\Modules\AgentForge\Config\AgentForgeGlobalConfig;
use OpenEMR\Modules\AgentForge\Ingest\DocumentIngestService;

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
