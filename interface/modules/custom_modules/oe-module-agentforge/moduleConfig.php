<?php

declare(strict_types=1);

return [
    'name' => 'AgentForge Launch Integration',
    'description' => 'Provides an OpenEMR launch entry point for the AgentForge copilot sidecar.',
    'version' => '0.1.0',
    'author' => 'GauntletAI',
    'email' => 'support@gauntletai.com',
    'license' => 'GPL-3.0',
    'acl_category' => 'patients',
    'acl_section' => 'demographics',
    'require' => [
        'openemr' => '>=7.0.0',
    ],
    'menu' => [],
];
