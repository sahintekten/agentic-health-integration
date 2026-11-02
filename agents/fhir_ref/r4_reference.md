# FHIR R4 quick reference: Patient, Observation, Encounter

## Common data types

- Identifier: system (uri), value (string), type (CodeableConcept, codes from http://terminology.hl7.org/CodeSystem/v2-0203 such as MR, PI, AN, VN), assigner (Reference, can be just a display)
- HumanName: use, text, family, given[] (first name, then middle names), prefix[], suffix[]
- Address: use, line[], city, district, state, postalCode, country
- ContactPoint: system (phone, email), value, use
- CodeableConcept: coding[] {system, code, display}, text
- Coding: system, code, display
- Quantity: value (decimal), unit (text), system (http://unitsofmeasure.org), code (UCUM code)
- Reference: reference (for example "Patient/123"), display
- Period: start, end (dateTime)
- date is YYYY-MM-DD. dateTime is YYYY-MM-DDThh:mm:ss with Z or +hh:mm. instant is a dateTime with seconds and zone.

## Patient

- identifier[] (Identifier)
- active (boolean)
- name[] (HumanName)
- telecom[] (ContactPoint)
- gender (code: male, female, other, unknown)
- birthDate (date)
- deceasedBoolean or deceasedDateTime
- address[] (Address)
- maritalStatus (CodeableConcept, http://terminology.hl7.org/CodeSystem/v3-MaritalStatus: A, D, I, L, M, P, S, T, U, W)
- multipleBirthBoolean or multipleBirthInteger
- contact[], communication[], generalPractitioner[], managingOrganization

## Observation

- identifier[] (Identifier)
- status (code: registered, preliminary, final, amended, corrected, cancelled, entered-in-error, unknown)
- category[] (CodeableConcept, http://terminology.hl7.org/CodeSystem/observation-category: vital-signs, laboratory, survey, social-history, imaging, exam, procedure, therapy, activity)
- code (CodeableConcept, usually LOINC http://loinc.org)
- subject (Reference(Patient))
- encounter (Reference(Encounter))
- effectiveDateTime or effectivePeriod
- issued (instant)
- performer[] (Reference)
- valueQuantity, valueString, valueCodeableConcept, valueBoolean, valueInteger
- dataAbsentReason (CodeableConcept)
- interpretation[] (CodeableConcept, http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation: L, N, H, LL, HH, A)
- note[], method, specimen, device
- referenceRange[] {low, high, text}
- component[] {code, valueQuantity, interpretation}

## Encounter

- identifier[] (Identifier)
- status (code: planned, arrived, triaged, in-progress, onleave, finished, cancelled, entered-in-error, unknown)
- statusHistory[] {status, period}
- class (Coding, http://terminology.hl7.org/CodeSystem/v3-ActCode: AMB, EMER, FLD, HH, IMP, ACUTE, NONAC, OBSENC, PRENC, SS, VR)
- type[] (CodeableConcept)
- serviceType (CodeableConcept)
- priority (CodeableConcept, http://terminology.hl7.org/CodeSystem/v3-ActPriority: A, EL, EM, P, R, S, UR)
- subject (Reference(Patient))
- participant[] {type[] (http://terminology.hl7.org/CodeSystem/v3-ParticipationType: ATND, ADM, REF, CON, DIS), period, individual (Reference(Practitioner))}
- period (Period)
- length (Duration)
- reasonCode[] (CodeableConcept)
- diagnosis[] {condition, use, rank}
- hospitalization {preAdmissionIdentifier, admitSource, reAdmission, dischargeDisposition (http://terminology.hl7.org/CodeSystem/discharge-disposition: home, alt-home, other-hcf, hosp, long, aadvice, exp, psy, rehab, snf, oth)}
- location[] {location (Reference(Location), can be just a display), status, period}
- serviceProvider (Reference(Organization))

## Practitioner

- identifier[], name[] (HumanName), telecom[], gender

## Code systems

- LOINC: http://loinc.org
- SNOMED CT: http://snomed.info/sct
- UCUM units: http://unitsofmeasure.org (for example mg/dL, mmol/L, mm[Hg], kg, cm, /min, %, 10*3/uL)
