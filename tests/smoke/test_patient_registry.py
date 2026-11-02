def test_health(registry):
    r = registry.get("/health")
    assert r.status_code == 200
    assert r.json()["status"] == "UP"


def test_list_patients(registry):
    body = registry.get("/patients", params={"pg": 1, "sz": 5}).json()
    assert body["tot"] > 0
    assert len(body["recs"]) == 5
    assert body["recs"][0]["pt_id"].startswith("MRN-")


def test_get_one_patient(registry):
    first = registry.get("/patients", params={"sz": 1}).json()["recs"][0]
    r = registry.get(f"/patients/{first['pt_id']}")
    assert r.status_code == 200
    assert r.json() == first


def test_unknown_patient_is_404(registry):
    assert registry.get("/patients/MRN-000000").status_code == 404
