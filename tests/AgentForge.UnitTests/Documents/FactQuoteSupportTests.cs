using FluentAssertions;
using AgentForge.Documents;

namespace AgentForge.UnitTests.Documents;

/// <summary>
/// Unit tests for <see cref="FactQuoteSupport"/>, the rule deciding whether a fact's own text is supported by
/// the quote it cites. <b>Failure mode guarded (regression, FR-CITE-2):</b> an invented fact paired with a
/// real quote - the quote is found on its page, so without this rule the invented text was stored at 1.0
/// with the real line's box. The rule errs towards "not supported": a false "not found" marks a real fact,
/// a false "found" launders an invented one.
/// </summary>
public sealed class FactQuoteSupportTests
{
    [Theory]
    [InlineData("Lisinopril (angioedema)", "Lisinopril (angioedema)")]
    [InlineData("Lisinopril (angioedema)", "Allergies: Lisinopril (angioedema)")]
    [InlineData("Metoprolol succinate", "Metoprolol succinate 50 mg daily")]
    [InlineData("50 mg daily", "Metoprolol succinate 50 mg daily")]
    [InlineData("12.5mg BID", "carvedilol 12.5 mg BID")]
    [InlineData("PENICILLIN (RASH)", "penicillin  (rash)")]
    [InlineData("Penicillin - rash", "Penicillin (rash)")]
    [InlineData("49/51mg", "sacubitril/valsartan 49/51 mg BID")]
    public void IsSupported_WhenTheQuoteCarriesTheTextAsAContiguousRunOfWords_IsTrue(string claimed, string quote)
    {
        // Given a quote that prints the claimed text, however it is cased, spaced or punctuated
        // When support is checked
        // Then the text is supported
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("1/2 tab", "Warfarin 1/2 tab daily")]
    [InlineData(".5 mg", "Warfarin .5 mg daily")]
    [InlineData("1½ tab", "Aspirin 1½ tab")]
    [InlineData("Father - stroke at 60", "Father - stroke at 60.")]           // sentence punctuation, not magnitude
    [InlineData("café", "café")]                               // NFC and NFD spell the same word
    public void IsSupported_WhenTheQuoteCarriesTheNumberAsWritten_IsTrue(string claimed, string quote)
    {
        // Given a number written with the punctuation that carries its magnitude, on both sides alike
        // When support is checked
        // Then it is supported
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("penicillin", "NO penicillin allergy")]
    [InlineData("chest pain", "denies chest pain")]
    public void IsSupported_WhenTheQuoteNegatesTheText_IsStillTrue_TheRuleIsContainmentNotAssertion(string claimed, string quote)
    {
        // a stated limit, pinned so that adding negation handling is a deliberate change.
        // The quote carries the words; whether it asserts them is not this rule's question.
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("Latex (anaphylaxis)", "Lisinopril (angioedema)")]          // an invented allergy on a real line
    [InlineData("Apixaban", "Warfarin 5 mg daily")]                           // an invented medication name
    [InlineData("50 mg daily", "Warfarin 5 mg daily")]                        // an invented dose
    [InlineData("5 mg", "Tamsulosin 0.5 mg daily")]                           // a decimal is one number, not two
    [InlineData("0.5 mg", "Warfarin 5 mg daily")]
    [InlineData("pen", "penicillin")]                                         // a fragment of a word is not the word
    [InlineData("rash penicillin", "penicillin (rash)")]                      // the words, but not as printed
    [InlineData("Lisinopril angioedema cough", "Lisinopril (angioedema)")]    // more than the quote says
    [InlineData("hydrochlorothiazide", "HCTZ 25mg")]                          // an expansion the page does not print
    [InlineData("5 mg", ".5 mg")]                                             // a naked leading decimal
    [InlineData("5 mg daily", "Warfarin .5 mg daily")]
    [InlineData("5 mg", "0,5 mg")]                                            // a decimal comma
    [InlineData("2 tab", "1/2 tab")]                                          // a fraction written with a slash
    [InlineData("1 tab", "1½ tab")]                                           // a vulgar fraction beside a digit
    [InlineData("½ tab", "1 tab")]                                            // a claim whose only number is a vulgar fraction
    [InlineData("cafe", "café")]                                    // NFD: the accent is part of the letter
    [InlineData("10 mg", "<10 mg")]                                           // a comparator is part of the number
    [InlineData("5 daily", "5% daily")]                                       // a percent sign is a word of its own
    public void IsSupported_WhenTheTextSaysSomethingTheQuoteDoesNot_IsFalse(string claimed, string quote)
    {
        // Given a quote that does not print the claimed text
        // When support is checked
        // Then the text is not supported - including the abbreviation case, deliberately: the rule prefers
        // marking a real fact over accepting an invented one
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("( - )")]
    public void IsSupported_WhenTheTextHasNoWordsAtAll_IsFalse(string claimed)
    {
        // Given a claimed text with nothing in it to compare
        // When support is checked against any quote
        // Then it is not supported: an empty claim grounds nothing, as an empty quote does not
        FactQuoteSupport.IsSupported(claimed, "Penicillin (rash) ( - )").Should().BeFalse();
    }

    // --- Lab results: analyte, value and unit --------------------------------------
    // A lab row is read the way it prints: the analyte's name, immediately followed by the value, then - when
    // a unit is claimed - the unit, with at most one flag between them. The name may be written in full or
    // as one of a small, closed set of abbreviations; nothing else is forgiven.

    [Theory]
    [InlineData("INR", "2.5", null, "INR 2.5")]
    [InlineData("Potassium", "5.9", "mmol/L", "Potassium 5.9 mmol/L 3.5-5.1 H")]
    [InlineData("BNP", "820", "pg/mL", "BNP 820 (H) pg/mL")]                               // one flag between value and unit
    [InlineData("HbA1c", "8.2", "%", "HbA1c 8.2 (H) %")]                                   // a unit that is only a symbol
    [InlineData("Glucose, fasting", "131", "mg/dL", "Glucose, fasting 131mg/dL 70-99 H")]
    [InlineData("eGFR", "52", "mL/min/1.73 m2", "eGFR 52 mL/min/1.73 m2 >=60 L")]
    [InlineData("INR", "3.4", null, "2026-08-04 INR 3.4 2.0-3.0 H")]                      // a date column before the name
    [InlineData("Troponin I", "<0.01", "ng/mL", "Troponin I <0.01 ng/mL")]                // a comparator, on both sides
    [InlineData("Potassium", "4.6", "mmol/L", "Sodium 139 Potassium 4.6 mmol/L")]         // the second row of a two-row quote
    [InlineData("INR", "2.5", "  ", "INR 2.5")]                                            // a blank unit claims nothing
    public void IsLabResultSupported_WhenTheQuotePrintsTheRowAsClaimed_IsTrue(string testName, string value, string? unit, string quote)
    {
        // Given a quote printing the analyte, then its value, then its unit
        // When support is checked
        // Then the result is supported
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("LDL Cholesterol", "168", "mg/dL", "LDL 168 (H) mg/dL")]
    [InlineData("HDL Cholesterol", "32", "mg/dL", "HDL 32 (L) mg/dL")]
    [InlineData("Potassium", "5.8", "mmol/L", "K+ 5.8 (H) mmol/L")]
    [InlineData("LDL", "151", "mg/dL", "LDL cholesterol 151 mg/dL <100 H")]               // either way round
    [InlineData("LDL cholesterol", "151", "mg/dL", "LDL-C 151 mg/dL")]
    public void IsLabResultSupported_WhenTheQuotePrintsAnAbbreviationInTheTable_IsTrue(string testName, string value, string? unit, string quote)
    {
        // Given a quote printing the analyte under one of the reviewed abbreviations for the claimed name
        // When support is checked
        // Then the result is supported: the table names the same analyte, it does not guess one
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("Potassium", "139", "mmol/L", "Sodium 139 mmol/L 135-145")]               // the value, filed under another analyte
    [InlineData("Sodium", "135", "mmol/L", "Sodium 136 mmol/L 135-145")]                  // a value read off the reference range
    [InlineData("Potassium", "139", "mmol/L", "Sodium 139 mmol/L Potassium 4.6 mmol/L")]  // the value from the other row
    [InlineData("Glucose", "96", "mmol/L", "Glucose 96 mg/dL")]                           // an invented unit
    [InlineData("Sodium", "139", "mg/dL", "Sodium 139 mmol/L Glucose 96 mg/dL")]          // the unit from the other row
    [InlineData("Sodium", "139", "mmol/L", "Sodium 139")]                                 // a unit the quote does not print
    [InlineData("HbA1c", "8.2", "%", "HbA1c 8.2")]
    [InlineData("BNP", "820", "pg/mL", "BNP 820 100-400 pg/mL")]                          // only a flag may sit between value and unit
    [InlineData("Digoxin", "5", "ng/mL", "Digoxin .5 ng/mL")]                             // magnitude is kept
    [InlineData("Digoxin", ".5", "ng/mL", "Digoxin 5 ng/mL")]
    [InlineData("Digoxin", "5", "ng/mL", "Digoxin 0,5 ng/mL")]
    [InlineData("INR", "2", null, "INR 1/2")]
    [InlineData("Troponin I", "0.01", "ng/mL", "Troponin I <0.01 ng/mL")]                 // a comparator changes the value
    [InlineData("eGFR", ">60", null, "eGFR 60")]
    [InlineData("HDL Cholesterol", "188", "mg/dL", "Non-HDL cholesterol 188 mg/dL <130 H")] // the tail of a longer name
    [InlineData("Potassium", "1.2", null, "Vitamin K 1.2")]
    [InlineData("Hemoglobin", "7.8", "%", "Hemoglobin A1c 7.8 % 4.0-5.6 H")]              // the head of a longer name
    [InlineData("Sodium", "128", "mmol/L", "Na 128 (L) mmol/L")]                          // an abbreviation the table does not hold
    [InlineData("LDL Cholesterol", "44", "mg/dL", "HDL cholesterol 44 mg/dL")]            // an abbreviation of another analyte
    [InlineData("LDL", "30", "mg/dL", "VLDL 30 mg/dL")]                                   // a fragment of a word is not the word
    [InlineData("Potassium", "5.9", "mmol/L", "5.9 mmol/L")]                              // a quote that never names the analyte
    [InlineData("Potassium", "5.9", "mmol/L", "Potassium measured at 5.9 mmol/L")]        // a name and value not printed together
    public void IsLabResultSupported_WhenTheQuoteDoesNotPrintTheRowAsClaimed_IsFalse(string testName, string value, string? unit, string quote)
    {
        // Given a quote that does not print this analyte, immediately followed by this value and this unit
        // When support is checked
        // Then the result is not supported: a false "not found" marks a real result, a false "found" would
        // store an invented one at full confidence
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeFalse();
    }

    // --- Residual false "found" routes from the review ----------------------

    [Theory]
    [InlineData("Troponin I", "0.01", "ng/mL", "Troponin I < 0.01 ng/mL")]                // 1: a comparator spaced off its number
    [InlineData("eGFR", "60", "mL/min/1.73m2", "eGFR > 60 mL/min/1.73m2")]
    [InlineData("eGFR", "60", null, "eGFR >= 60")]
    [InlineData("eGFR", "60", null, "eGFR ≥ 60")]
    [InlineData("Glucose", "100", "mg", "Glucose 100 mg/dL")]                             // 2: the head of the printed unit
    [InlineData("CRP", "5", "mg", "CRP 5 mg/L")]
    [InlineData("eGFR", "60", "mL/min", "eGFR 60 mL/min/1.73m2")]
    [InlineData("Glucose", "100", "mg", "Glucose 100 mg/dL (H)")]
    [InlineData("Hemoglobin", "9", "L", "Hemoglobin 9 L")]                                // 2: a low flag read as litres
    [InlineData("Hemoglobin", "9", "L", "Hemoglobin 9 L g/dL")]
    [InlineData("Potassium", "4.5", null, "WBC 7.2 K 4.5-11.0")]                          // 3: K printed as thousands
    [InlineData("Potassium", "4.5", null, "WBC 7.2 K 4.5 - 11.0")]
    [InlineData("K", "4.5", null, "WBC 7.2 K 4.5-11.0")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess -5 mmol/L")]                   // 4: a sign is part of the number
    [InlineData("Base excess", "5", "mmol/L", "Base excess −5 mmol/L")]
    [InlineData("Sodium", "135", null, "Sodium 135-145 136 mmol/L")]                      // 4: a range printed before the value
    [InlineData("Sodium", "135", null, "Sodium 135–145 136 mmol/L")]
    public void IsLabResultSupported_WhenTheQuotesNumberOrUnitIsNotTheClaimedOne_IsFalse(
        string testName, string value, string? unit, string quote)
    {
        // Given a quote whose words hold the claimed row only once a comparator, a sign, a unit's tail or a
        // range is dropped, or once a thousands K is read as potassium
        // When support is checked
        // Then the result is not supported
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeFalse();
    }

    [Theory]
    [InlineData("Troponin I", "<0.01", "ng/mL", "Troponin I < 0.01 ng/mL")]               // the comparator, spaced or not
    [InlineData("Troponin I", "< 0.01", "ng/mL", "Troponin I <0.01 ng/mL")]
    [InlineData("eGFR", ">60", "mL/min/1.73m2", "eGFR > 60 mL/min/1.73m2")]
    [InlineData("eGFR", ">=60", null, "eGFR >= 60")]
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg/dL(H)")]                       // a glued flag is not the unit's tail
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg/dL, Sodium 139 mmol/L")]
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg/dL Sodium 139 mmol/L")]
    [InlineData("eGFR", "60", "mL/min/1.73m2", "eGFR 60 mL/min/1.73m2")]
    [InlineData("Hemoglobin", "9", "g/L", "Hemoglobin 9 L g/L")]                          // a flag, then a unit ending in L
    [InlineData("Potassium", "4.5", "mmol/L", "K 4.5 mmol/L")]                             // a one-letter alias with its unit
    [InlineData("WBC", "7.2", "K/uL", "WBC 7.2 K/uL 4.5-11.0")]
    [InlineData("Base excess", "-5", "mmol/L", "Base excess -5 mmol/L")]
    [InlineData("Sodium", "136", "mmol/L", "Sodium 136 mmol/L 135-145")]
    public void IsLabResultSupported_WhenTheRowIsPrintedAsClaimed_IsStillTrue(
        string testName, string value, string? unit, string quote)
    {
        // Given a quote that prints the claimed row, comparator, sign and whole unit included
        // When support is checked
        // Then the result is supported: the stricter reading rejects only what the quote does not say
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeTrue();
    }

    [Fact]
    public void IsLabResultSupported_WhenAOneLetterAliasCarriesNoUnit_IsFalse()
    {
        // Given a potassium row printed as K with no unit - the layout a thousands K also prints
        // When support is checked with a blank unit
        // Then it is not supported: a stated false "not found", since a bare K cannot say which it is
        FactQuoteSupport.IsLabResultSupported("Potassium", "4.1", null, "K 4.1").Should().BeFalse();
    }

    [Theory]
    [InlineData("0.01 ng/mL", "< 0.01 ng/mL")]
    [InlineData("5 mmol/L", "-5 mmol/L")]
    public void IsSupported_WhenTheQuotesNumberOpensWithASpacedComparatorOrASign_IsFalse(string claimed, string quote)
    {
        // Given a quote whose number opens with a comparator or a minus sign
        // When support is checked for the bare number
        // Then it is not supported: intake facts share the same tokenising
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeFalse();
    }

    // --- Residual false "found" routes from the review ----------------------

    [Theory]
    [InlineData("Glucose", "100", "mg", "Glucose 100 mg / dL")]                           // 1: a spaced unit joiner
    [InlineData("Glucose", "100", "mg", "Glucose 100 mg /dL")]
    [InlineData("Glucose", "100", "mg", "Glucose 100 mg per dL")]                         // 1: a per joiner
    [InlineData("eGFR", "60", "mL/min", "eGFR 60 mL/min per 1.73 m2")]
    [InlineData("eGFR", "60", "mL/min", "eGFR 60 mL/min / 1.73 m2")]
    [InlineData("Sodium", "135", null, "Sodium 135—145 136")]                        // 2: an em-dash range before the value
    [InlineData("Sodium", "135", null, "Sodium 135 to 145 136")]                          // 2: a to range before the value
    [InlineData("Base excess", "5", "mmol/L", "Base excess-5 mmol/L")]                    // 3: a minus glued after a letter
    [InlineData("Base excess", "5", null, "Base excess-5")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess−5 mmol/L")]
    [InlineData("CA", "125", null, "CA-125 35 U/mL")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess –5 mmol/L")]                // 3: a minus spelt as another dash
    [InlineData("Base excess", "5", "mmol/L", "Base excess —5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess ‐5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess －5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess (–5) mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess: –5 mmol/L")]
    public void IsLabResultSupported_WhenTheQuoteJoinsTheUnitOnOrPrintsARangeOrASign_IsFalse(
        string testName, string value, string? unit, string quote)
    {
        // Given a quote whose words hold the claimed row only once a spaced or per unit joiner, an em-dash
        // or to range, or a minus glued to the name is read as a word break
        // When support is checked
        // Then the result is not supported
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeFalse();
    }

    [Theory]
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg / dL")]                        // the whole unit, spaced
    [InlineData("eGFR", "60", "mL/min per 1.73 m2", "eGFR 60 mL/min per 1.73 m2")]
    [InlineData("Sodium", "136", "mmol/L", "Sodium 136 mmol/L 135 to 145")]
    [InlineData("Sodium", "136", null, "Sodium 136 135—145")]
    [InlineData("CA-125", "35", "U/mL", "CA-125 35 U/mL")]
    [InlineData("Base excess", "-5", "mmol/L", "Base excess -5 mmol/L")]
    [InlineData("Base excess", "–5", "mmol/L", "Base excess –5 mmol/L")]
    public void IsLabResultSupported_WhenTheRowIsPrintedAsClaimedBesideAJoinerRangeOrDash_IsStillTrue(
        string testName, string value, string? unit, string quote)
    {
        // Given a quote that prints the claimed row, with a joiner, range or dash that belongs to it
        // When support is checked
        // Then the result is supported
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("Sodium", "136", "mmol/L", "Sodium-136 mmol/L")]                           // a value dashed onto its name
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg/dL / 5.6 mmol/L")]             // a unit followed by a spaced slash
    public void IsLabResultSupported_WhenTheRowIsPrintedInALayoutTheRuleCannotTellApart_IsFalse(
        string testName, string value, string? unit, string quote)
    {
        // Given a real row whose layout is also how a signed value or a longer unit prints
        // When support is checked
        // Then it is not supported: a stated false "not found", the safe direction
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeFalse();
    }

    [Theory]
    [InlineData("5 mg", "2.5—5 mg")]                                                 // an em-dash range is one number
    [InlineData("Metoprolol 50 mg", "Metoprolol -50 mg")]                                 // costs, all false "not found":
    [InlineData("2.5-5 mg", "2.5–5 mg")]                                             // a dash bullet reads as a sign,
    [InlineData("2.5-5 mg", "2.5 - 5 mg")]                                                // dash variants are not each other,
    [InlineData("-5 mg", "−5 mg")]
    [InlineData("-50 mg", "–50 mg")]
    [InlineData("50 mg", "25 mg -> 50 mg")]                                               // and an arrow's head is a comparator
    public void IsSupported_WhenTheQuotesNumberIsARangeOrCarriesASignOrComparator_IsFalse(string claimed, string quote)
    {
        // Given an intake quote whose number merges a range, a sign or a comparator the claim does not print
        // When support is checked
        // Then it is not supported: the costs are stated in ARCHITECTURE-DOCUMENTS.md section 7
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeFalse();
    }

    // --- Residual false "found" routes from the review ----------------------

    [Theory]
    [InlineData("Base excess", "5", "mmol/L", "Base excess ⁻5 mmol/L")]              // 1: a minus look-alike
    [InlineData("Base excess", "5", "mmol/L", "Base excess ₋5 mmol/L")]              //    that is not a dash
    [InlineData("Base excess", "5", "mmol/L", "Base excess ˗5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess ➖5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess ⁃5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess ­5 mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess (⁻5) mmol/L")]
    [InlineData("Base excess", "5", null, "Base excess: ➖5")]
    [InlineData("Sodium", "135", null, "Sodium 135 –145 136")]                            // 2: a range spaced on
    [InlineData("Sodium", "135", null, "Sodium 135– 145 136")]                            //    one side only
    [InlineData("Sodium", "135", null, "Sodium 135 -145 136")]
    [InlineData("Sodium", "135", null, "Sodium 135- 145 136")]
    [InlineData("Sodium", "135", null, "Sodium 135 ⁻145 136")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess 5– mmol/L")]                   // 3: a trailing minus
    [InlineData("Base excess", "5", "mmol/L", "Base excess 5- mmol/L")]
    [InlineData("Base excess", "5", null, "Base excess 5–")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess 5➖ mmol/L")]
    [InlineData("Base excess", "5", "mmol/L", "Base excess 5–mmol/L")]
    public void IsLabResultSupported_WhenTheQuotePrintsAMinusLookAlikeAHalfSpacedRangeOrATrailingMinus_IsFalse(
        string testName, string value, string? unit, string quote)
    {
        // Given a quote whose words hold the claimed row only once a minus look-alike, a dash spaced on one
        // side of a range, or a minus printed after the number is read as a word break
        // When support is checked
        // Then the result is not supported
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeFalse();
    }

    [Theory]
    [InlineData("Base excess", "⁻5", "mmol/L", "Base excess ⁻5 mmol/L")]        // the claim prints the same sign
    [InlineData("Sodium", "136", "mmol/L", "Sodium 136 mmol/L (135–145)")]
    [InlineData("Sodium", "136", "mmol/L", "Sodium 136 mmol/L 135 - 145")]
    [InlineData("Sodium", "136", null, "Sodium 136 135–145")]
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg/dL - fasting")]
    [InlineData("Glucose", "100", "mg/dL", "Glucose 100 mg/dL– fasting")]
    [InlineData("Hemo­globin", "13.5", "g/dL", "Hemo­globin 13.5 g/dL")]
    public void IsLabResultSupported_WhenTheRowIsPrintedAsClaimedBesideALookAlikeOrDash_IsStillTrue(
        string testName, string value, string? unit, string quote)
    {
        // Given a quote that prints the claimed row, with a sign, range or dash that belongs to it or to a
        // later word
        // When support is checked
        // Then the result is supported
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, quote).Should().BeTrue();
    }

    [Theory]
    [InlineData("5 mmol/L", "⁻5 mmol/L")]
    [InlineData("5 mg", "­5 mg")]
    [InlineData("10", "1.2 x 10⁻³")]                                               // a cost: an exponent's sign is kept
    public void IsSupported_WhenTheQuotesNumberOpensWithAMinusLookAlike_IsFalse(string claimed, string quote)
    {
        // Given an intake quote whose number opens with a minus look-alike that is not a Unicode dash
        // When support is checked for the unsigned number
        // Then it is not supported: intake facts share the same tokenising
        FactQuoteSupport.IsSupported(claimed, quote).Should().BeFalse();
    }

    [Fact]
    public void IsLabResultSupported_WhenAnyOneLetterNameCarriesNoUnit_IsFalse()
    {
        // Given a one-letter analyte name outside the alias table, printed with its value
        // When support is checked with a blank unit
        // Then it is not supported: every one-letter spelling needs a claimed unit, not only K
        FactQuoteSupport.IsLabResultSupported("P", "3.1", null, "P 3.1").Should().BeFalse();
    }

    [Theory]
    [InlineData("", "2.5", null)]
    [InlineData("INR", "", null)]
    [InlineData("INR", " - ", null)]
    [InlineData("INR", "2.5", "*")]                                                         // a unit with nothing to compare
    public void IsLabResultSupported_WhenANameValueOrUnitHasNoWordsAtAll_IsFalse(string testName, string value, string? unit)
    {
        // Given a claim with nothing in one of its parts to compare
        // When support is checked against a quote that prints INR 2.5
        // Then it is not supported: an empty claim grounds nothing
        FactQuoteSupport.IsLabResultSupported(testName, value, unit, "INR 2.5 * -").Should().BeFalse();
    }
}
