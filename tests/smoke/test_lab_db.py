import pytest


@pytest.mark.parametrize("table", ["LB_PT", "LB_TST_CODES", "LB_RSLT"])
def test_table_has_rows(lab_db, table):
    count = lab_db.execute(f"SELECT count(*) FROM {table}").fetchone()[0]
    assert count > 0


def test_results_link_to_test_codes(lab_db):
    row = lab_db.execute("SELECT count(*) FROM LB_RSLT r JOIN LB_TST_CODES c ON c.TST_CD = r.TST_CD").fetchone()
    assert row[0] > 0
