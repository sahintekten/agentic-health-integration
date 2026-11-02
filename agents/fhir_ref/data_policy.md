# Data policy

- Date-times without a time zone offset (for example HL7 v2 timestamps like `20160112204423`) are UTC.
  Assume UTC, write them with `Z`, and flag the value as assumed in the `timezone_assumption` data rule.
- Dates without a time stay dates (`YYYY-MM-DD`).
