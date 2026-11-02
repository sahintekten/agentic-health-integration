// Firely SDK 6.8 (Hl7.Fhir.R4) examples. All of this compiles on .NET 8.
using Hl7.Fhir.Model;

var p = new Patient { Id = "42" };
p.Identifier.Add(new Identifier("http://hospital.example.org/fhir/sid/registry-mrn", "MRN-000042")
{
    Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MR"),
    Assigner = new ResourceReference { Display = "HOSP" },
});
p.Name.Add(new HumanName { Family = "Testfamily", Given = new[] { "Testgiven", "Testmiddle" } });
p.Gender = AdministrativeGender.Male;
p.BirthDate = "1985-03-07";
p.Deceased = new FhirDateTime("2020-01-01");
p.Address.Add(new Address { Line = new[] { "1 Test Street" }, City = "Testtown", State = "MA", PostalCode = "02118", Country = "USA" });
p.MaritalStatus = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", "M");

var o = new Observation
{
    Id = "500001",
    Status = ObservationStatus.Final,
    Code = new CodeableConcept("http://loinc.org", "2339-0", "Glucose", null),
    Subject = new ResourceReference("Patient/42"),
    Effective = new FhirDateTime(DateTimeOffset.FromUnixTimeSeconds(1452631463)),
    Value = new Quantity { Value = 5.4m, Unit = "mg/dL", System = "http://unitsofmeasure.org", Code = "mg/dL" },
};
o.Identifier.Add(new Identifier("urn:x", "500001"));
o.Code.Coding.Add(new Coding("http://hospital.example.org/fhir/CodeSystem/lab-test-code", "GLU", "GLUCOSE"));
o.Category.Add(new CodeableConcept("http://terminology.hl7.org/CodeSystem/observation-category", "laboratory"));
o.Interpretation.Add(new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation", "H"));

var e = new Encounter
{
    Id = "V1000001",
    Status = Encounter.EncounterStatus.Finished,
    Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IMP"),
    Subject = new ResourceReference("Patient/42"),
    Period = new Period { Start = "2016-01-12T20:44:23Z", End = "2016-01-13T08:00:00Z" },
    Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-ActPriority", "EM"),
    ServiceType = new CodeableConcept { Text = "MED" },
    Hospitalization = new Encounter.HospitalizationComponent
    {
        DischargeDisposition = new CodeableConcept("http://terminology.hl7.org/CodeSystem/discharge-disposition", "home"),
    },
};
e.Identifier.Add(new Identifier("urn:visit", "V1000001"));
e.Type.Add(new CodeableConcept("http://snomed.info/sct", "50849002", "Emergency room admission", null));
e.ReasonCode.Add(new CodeableConcept("http://snomed.info/sct", "125605004"));
e.Location.Add(new Encounter.LocationComponent { Location = new ResourceReference { Display = "WARD^001^A^HOSP" } });
var part = new Encounter.ParticipantComponent { Individual = new ResourceReference { Display = "Testdoctor, Ann" } };
part.Type.Add(new CodeableConcept("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", "ATND"));
e.Participant.Add(part);
