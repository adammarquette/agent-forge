<?php

declare(strict_types=1);

$launchUri = getenv('AGENTFORGE_LAUNCH_URI');
$issuer = getenv('AGENTFORGE_ISSUER');
$patientId = filter_input(INPUT_GET, 'patient') ?: null;

if (!is_string($launchUri) || $launchUri === '' || !is_string($issuer) || $issuer === '') {
    http_response_code(500);
    exit('Launch configuration is missing.');
}

$query = http_build_query([
    'launch' => filter_input(INPUT_GET, 'launch') ?: '',
    'iss' => $issuer,
    'aud' => $issuer,
    'patient' => $patientId,
], '', '&', PHP_QUERY_RFC3986);

header('Location: ' . $launchUri . '?' . rtrim($query, '&'));
exit;
