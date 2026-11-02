# Patient ID policy

All adapters use one shared patient key: the **hospital number**.

- It is an integer, written without leading zeros (for example `4521`, never `004521`).
- `Patient.id` is the hospital number as a string.
- Every `subject` reference points to `Patient/<hospital number>`.
- `Patient.identifier` keeps the original source id as well, with the source system's identifier URI.

How each kind of source id becomes the hospital number:

- A prefixed id such as `MRN-004521`: remove the prefix and the leading zeros.
- A plain numeric id such as `4521`: use it as is.
- An HL7 v2 id field with components: take the numeric component and remove leading zeros.

If a source id cannot be turned into a number, keep the record, use the original id as `Patient.id`, and count it as an unlinked patient.
