# Backup coverage

Open **Inventory → Backup coverage...**, add inventory databases, and review the
device label on every root. Defaults come from recorded filesystem IDs. Assign
the same label to all roots and exported databases from the same physical device.
Labels are user declarations, not proof that two drives are physically independent.

Choose **Analyze coverage**. Filters show content recorded on only one device,
content recorded on every selected device, all verified content, or unverified
entries. Select a row to inspect all recorded locations. Multiple copies on one
device count once. Names do not need to match: content matches by size and full
SHA-256 digest.

Each inventory shows its latest scan status, scan age, and local root availability.
Offline roots can contribute historical coverage. Missing or stale hashes and
entries from incomplete scans appear as unverified. Missing files, links, linked
descendants, and stored exclusions do not contribute. No file contents are read
and input databases stay read-only.

The command line accepts repeated device/database pairs and includes every root
in each selected database:

```bash
bn coverage --inventory NAS=nas.db --inventory D1=external.db
bn coverage --inventory NAS=nas.db --inventory D1=external.db --json
```

These are snapshot reports. A single-device result means no second device is
confirmed among these inputs; unverified entries may still contain another copy.
An everywhere result refers to the selected device labels and recorded scans.
Refresh and hash inventories to establish their current contents.
