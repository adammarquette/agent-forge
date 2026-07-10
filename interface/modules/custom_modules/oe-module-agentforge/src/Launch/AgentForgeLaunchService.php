<?php

declare(strict_types=1);

namespace OpenEMR\Modules\AgentForge\Launch;

final class AgentForgeLaunchService
{
    public function buildLaunchUrl(string $launchToken, string $issuer, string $launchUri, ?string $patientId = null): string
    {
        $query = http_build_query([
            'launch' => $launchToken,
            'iss' => $issuer,
            'aud' => $issuer,
            'patient' => $patientId,
        ], '', '&', PHP_QUERY_RFC3986);

        return $launchUri . '?' . rtrim($query, '&');
    }
}
