<?php

/**
 * Seed a SYNTHETIC cardiology demo cohort for AgentForge.
 *
 * Creates ~7 clearly-fake patients with cardiology-realistic charts (problems,
 * allergies, medications), a clinical timeline (two past encounters each with
 * vital-signs) and an appointment TODAY with a maintainer-created cardiologist,
 * so the AgentForge pre-visit brief / chat and the Daily Agenda roster have
 * meaningful data — including a "last visit" to anchor "what changed since".
 *
 * Idempotent, keyed on patient_data.pubpid (AF-DEMO-NN): a new patient gets the
 * full chart; an already-seeded patient keeps its chart and only has its
 * timeline backfilled (skipped entirely once it already has an encounter), so a
 * re-run enriches the existing cohort rather than duplicating or skipping it.
 *
 * SYNTHETIC / DEMO DATA ONLY — never real PHI (repo rule). Names, DOBs and
 * addresses below are invented.
 *
 * Writes to the tables AgentForge actually reads over FHIR (verified in
 * gitlab#36 / gitlab#39): problems + allergies -> `lists`; medications ->
 * `prescriptions` (NOT the medication list — FhirMedicationRequestService reads
 * PrescriptionService); encounters -> `form_encounter` (+ `forms`) and
 * vital-signs -> `form_vitals`, both via EncounterService; appointment ->
 * openemr_postcalendar_events with pc_aid = the provider's users.id, which is
 * what the Daily Agenda filters on.
 *
 * VALIDATION REQUIRED: this has not been run against a live OpenEMR DB. Run once
 * against a throwaway/staging instance and reconcile any service validation
 * messages (printed to STDERR) before trusting it for a demo — a wrong column or
 * an unmet validator requirement fails as a silent empty chart, not a crash.
 *
 * Usage (inside the OpenEMR container, as the web user - NOT root):
 *   su -s /bin/sh apache -c 'php \
 *     interface/modules/custom_modules/oe-module-agentforge/scripts/seed_cardiology_demo.php \
 *     --provider=<username>'             # the cardiologist created in Admin -> Users
 *   optional: --dry-run                  # print what would be created, write nothing
 *
 * reference: gitlab#36 (fork), agent-forge-copilot USERS.md UC-1/UC-6
 *
 * @package   OpenEMR
 * @author    AgentForge
 * @license   https://github.com/openemr/openemr/blob/master/LICENSE GNU General Public License 3
 */

declare(strict_types=1);

// CLI only - never web-reachable.
if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}

// globals.php is a web entry point: give it a site (and a host) so it does not
// reject the run as siteless ("invalid site id"), per OpenEMR's CLI convention
// (see contrib/util/*). Run this as the web user, e.g.
// `su -s /bin/sh apache -c 'php <this script> --provider=<user>'` - NOT root,
// which OpenEMR's RootCliGuard aborts.
// @phpstan-ignore openemr.forbiddenRequestGlobals
$_GET['site'] = 'default';
// @phpstan-ignore openemr.forbiddenRequestGlobals
$_SERVER['HTTP_HOST'] = 'localhost';
$ignoreAuth = true;
$sessionAllowWrite = true;
require_once __DIR__ . "/../../../../globals.php";

use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Common\Session\SessionWrapperFactory;
use OpenEMR\Common\Uuid\UuidRegistry;
use OpenEMR\Services\AppointmentService;
use OpenEMR\Services\EncounterService;
use OpenEMR\Services\ListService;
use OpenEMR\Services\PatientService;
use OpenEMR\Services\PrescriptionService;

// Closures, not named functions: the fork's PHPStan forbids declaring functions
// in the global namespace (openemr.noGlobalNsFunctions). DB rows are `mixed` to
// static analysis, so these narrow a single-row query result to an int by key
// (0 when absent/non-scalar), keeping the seed inside the strict ruleset
// (no sqlQuery(), no empty(), no casting mixed).
$afRowInt = (static fn(mixed $row, string $key): int => (is_array($row) && isset($row[$key]) && is_scalar($row[$key])) ? (int) $row[$key] : 0);

// Same, for the first row of a ProcessingResult::getData() list.
$afFirstRowInt = static function (mixed $rows, string $key): int {
    if (is_array($rows) && isset($rows[0]) && is_array($rows[0]) && isset($rows[0][$key]) && is_scalar($rows[0][$key])) {
        return (int) $rows[0][$key];
    }

    return 0;
};

$options = getopt('', ['provider:', 'dry-run']);
$providerUsername = $options['provider'] ?? null;
$dryRun = array_key_exists('dry-run', $options);

if (!is_string($providerUsername) || $providerUsername === '') {
    fwrite(STDERR, "Usage: php seed_cardiology_demo.php --provider=<username> [--dry-run]\n");
    exit(1);
}

// Resolve the cardiologist's users.id — appointments (pc_aid) and prescriptions
// (provider_id) reference it, and the Daily Agenda filters the roster by it.
$providerRow = QueryUtils::querySingleRow("SELECT id FROM users WHERE username = ? AND active = 1", [$providerUsername]);
$providerId = $afRowInt($providerRow, 'id');
if ($providerId === 0) {
    fwrite(STDERR, "Provider user '$providerUsername' not found or inactive. Create it in Admin -> Users first.\n");
    exit(1);
}

// The vital-signs POST_SAVE listener (VitalsCalculatedService) reads the
// session's authUserID and hard-types it as int; in this CLI context none is
// set, so a fresh (DR) seed would throw on every vitals save. Bind the session
// to the seeding cardiologist so those writes attribute cleanly.
SessionWrapperFactory::getInstance()->getActiveSession()->set('authUserID', $providerId);

$facilityRow = QueryUtils::querySingleRow("SELECT id, name FROM facility ORDER BY id LIMIT 1");
$facilityId = $afRowInt($facilityRow, 'id') ?: 3;
$facilityName = (is_array($facilityRow) && isset($facilityRow['name']) && is_string($facilityRow['name'])) ? $facilityRow['name'] : 'Your Clinic Name Here';

$catRow = QueryUtils::querySingleRow("SELECT pc_catid FROM openemr_postcalendar_categories WHERE pc_catname LIKE 'Office Visit' LIMIT 1");
$officeVisitCatId = $afRowInt($catRow, 'pc_catid') ?: 5;

/**
 * Synthetic cardiology cohort. Each problem/allergy is an ICD-10-coded lists row;
 * each med is a prescriptions row (drug + free-text dosage). Appointment times are
 * assigned relative to "now" at seed time so a fresh seed yields an upcoming roster.
 */
$cohort = [
    [
        'fname' => 'Marcus', 'lname' => 'Demopatient', 'dob' => '1957-03-11', 'sex' => 'Male',
        'reason' => 'Paroxysmal AFib — rate control and anticoagulation review',
        'problems' => [
            ['title' => 'Paroxysmal atrial fibrillation', 'icd10' => 'ICD10:I48.0', 'since' => '2021-06-01'],
            ['title' => 'Essential hypertension', 'icd10' => 'ICD10:I10', 'since' => '2015-01-01'],
            ['title' => 'Type 2 diabetes mellitus', 'icd10' => 'ICD10:E11.9', 'since' => '2018-09-01'],
        ],
        'allergies' => ['Penicillin'],
        'meds' => [
            ['drug' => 'Apixaban', 'dosage' => '5 mg PO BID'],
            ['drug' => 'Metoprolol succinate', 'dosage' => '100 mg PO daily'],
            ['drug' => 'Metformin', 'dosage' => '1000 mg PO BID'],
        ],
    ],
    [
        'fname' => 'Eleanor', 'lname' => 'Testcase', 'dob' => '1949-11-23', 'sex' => 'Female',
        'reason' => 'HFrEF (EF ~30%) — GDMT titration',
        'problems' => [
            ['title' => 'Heart failure with reduced ejection fraction', 'icd10' => 'ICD10:I50.22', 'since' => '2020-02-01'],
            ['title' => 'Chronic kidney disease, stage 3', 'icd10' => 'ICD10:N18.30', 'since' => '2019-05-01'],
        ],
        'allergies' => ['Lisinopril (angioedema)'],
        'meds' => [
            ['drug' => 'Sacubitril/valsartan', 'dosage' => '49/51 mg PO BID'],
            ['drug' => 'Carvedilol', 'dosage' => '25 mg PO BID'],
            ['drug' => 'Spironolactone', 'dosage' => '25 mg PO daily'],
            ['drug' => 'Empagliflozin', 'dosage' => '10 mg PO daily'],
            ['drug' => 'Furosemide', 'dosage' => '40 mg PO daily'],
        ],
    ],
    [
        'fname' => 'Raymond', 'lname' => 'Sampleton', 'dob' => '1962-07-04', 'sex' => 'Male',
        'reason' => 'CAD s/p PCI — DAPT and lipid management',
        'problems' => [
            ['title' => 'Coronary artery disease, native vessel', 'icd10' => 'ICD10:I25.10', 'since' => '2023-01-15'],
            ['title' => 'Hyperlipidemia', 'icd10' => 'ICD10:E78.5', 'since' => '2016-03-01'],
        ],
        'allergies' => [],
        'meds' => [
            ['drug' => 'Aspirin', 'dosage' => '81 mg PO daily'],
            ['drug' => 'Ticagrelor', 'dosage' => '90 mg PO BID'],
            ['drug' => 'Atorvastatin', 'dosage' => '80 mg PO daily'],
        ],
    ],
    [
        'fname' => 'Yolanda', 'lname' => 'Mockford', 'dob' => '1954-01-30', 'sex' => 'Female',
        'reason' => 'Resistant hypertension — regimen review',
        'problems' => [
            ['title' => 'Resistant essential hypertension', 'icd10' => 'ICD10:I10', 'since' => '2012-08-01'],
            ['title' => 'Chronic kidney disease, stage 3', 'icd10' => 'ICD10:N18.30', 'since' => '2021-11-01'],
        ],
        'allergies' => ['Sulfa drugs'],
        'meds' => [
            ['drug' => 'Chlorthalidone', 'dosage' => '25 mg PO daily'],
            ['drug' => 'Amlodipine', 'dosage' => '10 mg PO daily'],
            ['drug' => 'Losartan', 'dosage' => '100 mg PO daily'],
        ],
    ],
    [
        'fname' => 'Gerald', 'lname' => 'Placeholder', 'dob' => '1945-09-17', 'sex' => 'Male',
        'reason' => 'Long-standing AFib on warfarin — INR management',
        'problems' => [
            ['title' => 'Chronic atrial fibrillation', 'icd10' => 'ICD10:I48.2', 'since' => '2014-04-01'],
            ['title' => 'Heart failure with preserved ejection fraction', 'icd10' => 'ICD10:I50.32', 'since' => '2022-06-01'],
        ],
        'allergies' => [],
        'meds' => [
            ['drug' => 'Warfarin', 'dosage' => '5 mg PO daily, per INR'],
            ['drug' => 'Diltiazem', 'dosage' => '240 mg PO daily'],
        ],
    ],
    [
        'fname' => 'Priya', 'lname' => 'Examplewicz', 'dob' => '1966-12-05', 'sex' => 'Female',
        'reason' => 'HFpEF with T2DM and obesity — follow-up',
        'problems' => [
            ['title' => 'Heart failure with preserved ejection fraction', 'icd10' => 'ICD10:I50.32', 'since' => '2021-03-01'],
            ['title' => 'Type 2 diabetes mellitus', 'icd10' => 'ICD10:E11.9', 'since' => '2017-01-01'],
            ['title' => 'Obesity', 'icd10' => 'ICD10:E66.9', 'since' => '2015-01-01'],
        ],
        'allergies' => ['Codeine (nausea)'],
        'meds' => [
            ['drug' => 'Empagliflozin', 'dosage' => '10 mg PO daily'],
            ['drug' => 'Metformin', 'dosage' => '1000 mg PO BID'],
            ['drug' => 'Furosemide', 'dosage' => '20 mg PO daily'],
        ],
    ],
    [
        'fname' => 'Curtis', 'lname' => 'Demoford', 'dob' => '1959-05-28', 'sex' => 'Male',
        'reason' => 'Post-cardioversion follow-up',
        'problems' => [
            ['title' => 'Persistent atrial fibrillation, post-cardioversion', 'icd10' => 'ICD10:I48.1', 'since' => '2024-11-01'],
            ['title' => 'Essential hypertension', 'icd10' => 'ICD10:I10', 'since' => '2019-02-01'],
        ],
        'allergies' => [],
        'meds' => [
            ['drug' => 'Apixaban', 'dosage' => '5 mg PO BID'],
            ['drug' => 'Amiodarone', 'dosage' => '200 mg PO daily'],
            ['drug' => 'Lisinopril', 'dosage' => '20 mg PO daily'],
        ],
    ],
];

// Vital-sign baselines, index-aligned with $cohort and tuned to each patient's
// cardiology picture. US units as form_vitals stores them: weight lb, height in,
// temperature degF. The two dated encounters below vary these slightly so the
// brief has a "since last visit" trend to diff (e.g. the diuresed HF patients
// come down in weight, the resistant-HTN patient stays high).
$vitalsBaseline = [
    ['height' => 70, 'weight' => 208, 'bps' => 134, 'bpd' => 84, 'pulse' => 86, 'resp' => 16, 'temp' => 98.2], // Marcus — AFib/HTN/DM
    ['height' => 63, 'weight' => 172, 'bps' => 108, 'bpd' => 66, 'pulse' => 78, 'resp' => 18, 'temp' => 98.0], // Eleanor — HFrEF
    ['height' => 71, 'weight' => 196, 'bps' => 122, 'bpd' => 76, 'pulse' => 64, 'resp' => 15, 'temp' => 98.4], // Raymond — CAD s/p PCI
    ['height' => 62, 'weight' => 178, 'bps' => 158, 'bpd' => 92, 'pulse' => 74, 'resp' => 16, 'temp' => 98.1], // Yolanda — resistant HTN
    ['height' => 69, 'weight' => 184, 'bps' => 126, 'bpd' => 72, 'pulse' => 70, 'resp' => 16, 'temp' => 98.3], // Gerald — chronic AFib/HFpEF
    ['height' => 64, 'weight' => 214, 'bps' => 138, 'bpd' => 80, 'pulse' => 82, 'resp' => 17, 'temp' => 98.2], // Priya — HFpEF/DM/obesity
    ['height' => 70, 'weight' => 199, 'bps' => 128, 'bpd' => 78, 'pulse' => 68, 'resp' => 15, 'temp' => 98.4], // Curtis — post-cardioversion
];

// Two past encounters per patient (days-ago), oldest first; the last element is
// the "last visit" the brief anchors "what changed" against. The older visit
// runs slightly higher/heavier so there's a visible trend into the recent one.
$encounterDaysAgo = [180, 90];
$afVitalsForVisit = static fn(array $base, bool $isOlder): array => [
    'bps' => $base['bps'] + ($isOlder ? 8 : 0),
    'bpd' => $base['bpd'] + ($isOlder ? 4 : 0),
    'pulse' => $base['pulse'] + ($isOlder ? 6 : 0),
    'weight' => $base['weight'] + ($isOlder ? 4 : 0),
    'height' => $base['height'],
    'respiration' => $base['resp'],
    'temperature' => $base['temp'],
];

$patientService = new PatientService();
$listService = new ListService();
$prescriptionService = new PrescriptionService();
$appointmentService = new AppointmentService();
$encounterService = new EncounterService();

$seeded = 0;
$skipped = 0;
$encountersAdded = 0;

foreach ($cohort as $index => $p) {
    $pubpid = sprintf('AF-DEMO-%02d', $index + 1);

    $existing = QueryUtils::querySingleRow("SELECT pid FROM patient_data WHERE pubpid = ?", [$pubpid]);
    $pid = $afRowInt($existing, 'pid');
    $patientExisted = $pid > 0;

    // Appointment time: upcoming relative to now, staggered, so a fresh seed
    // produces a roster the Daily Agenda (which only shows not-yet-occurred
    // appointments) will actually render.
    $apptTs = strtotime("+" . (15 + $index * 20) . " minutes");
    $apptTime = date('H:i:s', $apptTs);
    $apptDate = date('Y-m-d', $apptTs);

    if ($dryRun) {
        $verb = $patientExisted ? "would backfill (exists pid $pid)" : "would seed";
        echo "$verb $pubpid — {$p['fname']} {$p['lname']} · {$p['reason']} · +2 encounters w/ vitals\n";
        $seeded++;
        continue;
    }

    // Create the patient core (demographics, problems, allergies, meds, today's
    // appointment) only for a brand-new patient; an already-seeded one keeps
    // its chart and just gets the clinical timeline backfilled below.
    if (!$patientExisted) {
        $result = $patientService->insert([
            'fname' => $p['fname'],
            'lname' => $p['lname'],
            'DOB' => $p['dob'],
            'sex' => $p['sex'],
            'street' => (100 + $index) . ' Sample Street',
            'city' => 'Springfield',
            'state' => 'IL',
            'postal_code' => '62704',
            'pubpid' => $pubpid,
        ]);
        $pid = $afFirstRowInt($result->getData(), 'pid');
        if ($pid === 0) {
            fwrite(STDERR, "FAILED $pubpid patient insert: " . json_encode($result->getValidationMessages()) . "\n");
            continue;
        }

        foreach ($p['problems'] as $problem) {
            $listService->insert([
                'pid' => $pid,
                'type' => 'medical_problem',
                'title' => $problem['title'],
                'begdate' => $problem['since'],
                'enddate' => null,
                'diagnosis' => $problem['icd10'],
            ]);
        }

        foreach ($p['allergies'] as $allergen) {
            $listService->insert([
                'pid' => $pid,
                'type' => 'allergy',
                'title' => $allergen,
                'begdate' => date('Y-m-d'),
                'enddate' => null,
                'diagnosis' => '',
            ]);
        }

        foreach ($p['meds'] as $med) {
            $prescriptionService->insert([
                'patient_id' => $pid,
                'drug' => $med['drug'],
                'dosage' => $med['dosage'],
                'start_date' => date('Y-m-d'),
                'provider_id' => $providerId,
                'active' => 1,
            ]);
        }

        $appointmentService->insert($pid, [
            'pc_catid' => $officeVisitCatId,
            'pc_title' => 'Cardiology follow-up',
            'pc_duration' => 1800,
            'pc_hometext' => $p['reason'],
            'pc_eventDate' => $apptDate,
            'pc_startTime' => $apptTime,
            'pc_apptstatus' => '-',
            'pc_facility' => $facilityId,
            'pc_billing_location' => $facilityId,
            'pc_aid' => $providerId,
        ]);

        echo "seed  $pubpid — {$p['fname']} {$p['lname']} (pid $pid) · appt $apptDate $apptTime\n";
        $seeded++;
    } else {
        echo "exist $pubpid — {$p['fname']} {$p['lname']} (pid $pid) · backfilling timeline\n";
        $skipped++;
    }

    // Clinical timeline (encounters + vital-signs). Idempotent on "already has
    // an encounter" so a re-run never stacks duplicate visits onto a patient.
    $hasEncounter = $afRowInt(
        QueryUtils::querySingleRow("SELECT encounter FROM form_encounter WHERE pid = ? LIMIT 1", [$pid]),
        'encounter'
    );
    if ($hasEncounter > 0) {
        echo "      timeline already present — skipping encounters/vitals\n";
        continue;
    }

    // insertEncounter keys on the patient UUID, not the pid.
    $puuidRow = QueryUtils::querySingleRow("SELECT uuid FROM patient_data WHERE pid = ?", [$pid]);
    $puuidBin = (is_array($puuidRow) && isset($puuidRow['uuid']) && is_string($puuidRow['uuid'])) ? $puuidRow['uuid'] : '';
    if ($puuidBin === '') {
        fwrite(STDERR, "SKIP $pubpid timeline — no patient uuid for pid $pid\n");
        continue;
    }
    $puuid = UuidRegistry::uuidToString($puuidBin);
    $base = $vitalsBaseline[$index] ?? $vitalsBaseline[0];

    foreach ($encounterDaysAgo as $vi => $daysAgo) {
        $visitTs = strtotime("-$daysAgo days");
        $visitDate = date('Y-m-d H:i:s', $visitTs);

        $encResult = $encounterService->insertEncounter($puuid, [
            'date' => $visitDate,
            'reason' => $p['reason'],
            'facility' => $facilityName,
            'facility_id' => $facilityId,
            'billing_facility' => $facilityId,
            'pc_catid' => $officeVisitCatId,
            'class_code' => 'AMB',
            'provider_id' => $providerId,
            'user' => $providerUsername,
            'group' => 'Default',
        ]);
        if (!$encResult->isValid() || !$encResult->hasData()) {
            fwrite(STDERR, "FAILED $pubpid encounter ({$daysAgo}d ago): " . json_encode($encResult->getValidationMessages()) . "\n");
            continue;
        }
        $encId = $afFirstRowInt($encResult->getData(), 'encounter');
        if ($encId === 0) {
            fwrite(STDERR, "FAILED $pubpid encounter ({$daysAgo}d ago): no encounter id returned\n");
            continue;
        }

        // Saving vitals fires the FHIR module's calculated-observation subscriber
        // (mean-BP derivation). On a patient with >1 vitals it logs a benign
        // "Failed to save calculated record" (a duplicate on its own join table) -
        // harmless here: the raw vital-signs the brief reads still save, and the
        // derived mean-BP observation is not one AgentForge consumes.
        $vitals = $afVitalsForVisit($base, $vi === 0);
        $vitals['date'] = $visitDate;
        $encounterService->insertVital($pid, $encId, $vitals);
        $encountersAdded++;

        echo "      encounter $encId @ " . date('Y-m-d', $visitTs)
            . " · BP {$vitals['bps']}/{$vitals['bpd']} HR {$vitals['pulse']} Wt {$vitals['weight']}lb\n";
    }
}

echo "\nDone. seeded=$seeded backfilled/skipped=$skipped encounters=$encountersAdded provider=$providerUsername (users.id $providerId)\n";
exit(0);
