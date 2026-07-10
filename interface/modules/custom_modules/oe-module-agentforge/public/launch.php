<?php

declare(strict_types=1);

$launchUri = getenv('AGENTFORGE_LAUNCH_URI') ?: 'https://copilot.example.com/launch';
$issuer = getenv('AGENTFORGE_ISSUER') ?: 'https://openemr.example.com/fhir';
$patientId = $_GET['patient'] ?? null;

if (empty($launchUri) || empty($issuer)) {
    http_response_code(500);
    exit('Launch configuration is missing.');
}

$query = http_build_query([
    'launch' => $_GET['launch'] ?? '',
    'iss' => $issuer,
    'aud' => $issuer,
    'patient' => $patientId,
], '', '&', PHP_QUERY_RFC3986);

header('Location: ' . $launchUri . '?' . rtrim($query, '&'));
exit;
