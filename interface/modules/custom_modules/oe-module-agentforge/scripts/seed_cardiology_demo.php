<?php

/**
 * Seed a SYNTHETIC cardiology demo cohort for AgentForge.
 *
 * Creates ~7 clearly-fake patients with cardiology-realistic charts (problems,
 * allergies, medications) and an appointment TODAY with a maintainer-created
 * cardiologist, so the AgentForge pre-visit brief / chat and the Daily Agenda
 * roster have meaningful data. Idempotent: keyed on patient_data.pubpid
 * (AF-DEMO-NN); re-running skips patients that already exist.
 *
 * SYNTHETIC / DEMO DATA ONLY — never real PHI (repo rule). Names, DOBs and
 * addresses below are invented.
 *
 * Writes to the tables AgentForge actually reads over FHIR (verified in
 * gitlab#36): problems + allergies -> `lists`; medications -> `prescriptions`
 * (NOT the medication list — FhirMedicationRequestService reads PrescriptionService);
 * appointment -> openemr_postcalendar_events with pc_aid = the provider's users.id,
 * which is what the Daily Agenda filters on.
 *
 * VALIDATION REQUIRED: this has not been run against a live OpenEMR DB. Run once
 * against a throwaway/staging instance and reconcile any service validation
 * messages (printed to STDERR) before trusting it for a demo — a wrong column or
 * an unmet validator requirement fails as a silent empty chart, not a crash.
 *
 * Usage (inside the OpenEMR container):
 *   php interface/modules/custom_modules/oe-module-agentforge/scripts/seed_cardiology_demo.php \
 *       --provider=<username>            # the cardiologist you created in Admin -> Users
 *   optional: --dry-run                  # print what would be created, write nothing
 *
 * reference: gitlab#36 (fork), agent-forge-copilot USERS.md UC-1/UC-6
 *
 * @package   OpenEMR
 * @author    AgentForge
 * @license   https://github.com/openemr/openemr/blob/master/LICENSE GNU General Public License 3
 */

declare(strict_types=1);

// CLI bootstrap: no interactive login. Run inside the container as a trusted script.
$ignoreAuth = true;
$sessionAllowWrite = true;
require_once __DIR__ . "/../../../../globals.php";

use OpenEMR\Common\Database\QueryUtils;
use OpenEMR\Services\AppointmentService;
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

$facilityRow = QueryUtils::querySingleRow("SELECT id FROM facility ORDER BY id LIMIT 1");
$facilityId = $afRowInt($facilityRow, 'id') ?: 3;

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

$patientService = new PatientService();
$listService = new ListService();
$prescriptionService = new PrescriptionService();
$appointmentService = new AppointmentService();

$seeded = 0;
$skipped = 0;

foreach ($cohort as $index => $p) {
    $pubpid = sprintf('AF-DEMO-%02d', $index + 1);

    $existing = QueryUtils::querySingleRow("SELECT pid FROM patient_data WHERE pubpid = ?", [$pubpid]);
    $existingPid = $afRowInt($existing, 'pid');
    if ($existingPid > 0) {
        echo "skip  $pubpid — already present (pid $existingPid)\n";
        $skipped++;
        continue;
    }

    // Appointment time: upcoming relative to now, staggered, so a fresh seed
    // produces a roster the Daily Agenda (which only shows not-yet-occurred
    // appointments) will actually render.
    $apptTs = strtotime("+" . (15 + $index * 20) . " minutes");
    $apptTime = date('H:i:s', $apptTs);
    $apptDate = date('Y-m-d', $apptTs);

    if ($dryRun) {
        echo "would seed $pubpid — {$p['fname']} {$p['lname']} · {$p['reason']} · appt $apptDate $apptTime\n";
        $seeded++;
        continue;
    }

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
}

echo "\nDone. seeded=$seeded skipped=$skipped provider=$providerUsername (users.id $providerId)\n";
exit(0);
