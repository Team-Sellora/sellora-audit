-- US-E7-1-Q4 - tamper detection, by hand. ONLY on a local or copied audit_db, AS THE OWNER ROLE (audit_owner).
-- audit_log_tests.py --target local --allow-tamper does the same and restores the rows automatically.
--   psql "host=localhost port=<port> dbname=audit_db user=audit_owner" -v company=<company-guid> -v pos=10 -f audit-tamper.sql
-- Then call GET /api/audit/verify as the Company Admin:
--   step 1 -> verified false, firstBroken.chainPosition = :pos, reason hash mismatch
--   step 2 -> verified false, firstBroken.chainPosition = :pos + 2, reason missing position
\set ON_ERROR_STOP on

\echo '== keep copies so the rows can be put back (run the RESTORE block at the end)'
CREATE TABLE IF NOT EXISTS public.qa_tamper_backup AS SELECT * FROM audit.audit_entry WHERE false;
INSERT INTO public.qa_tamper_backup
SELECT * FROM audit.audit_entry WHERE company_id = :'company' AND chain_position IN (:pos, :pos + 2);

\echo '== step 1: change one payload'
UPDATE audit.audit_entry SET payload = payload || ' '
WHERE company_id = :'company' AND chain_position = :pos;

\echo '== now call GET /api/audit/verify, then continue with step 2'
\prompt 'Press Enter for step 2 ' dummy

\echo '== step 2: delete another entry'
DELETE FROM audit.audit_entry WHERE company_id = :'company' AND chain_position = :pos + 2;

\echo '== call GET /api/audit/verify again'
\prompt 'Press Enter to RESTORE both rows ' dummy

\echo '== RESTORE'
UPDATE audit.audit_entry e SET payload = b.payload FROM public.qa_tamper_backup b
WHERE e.audit_entry_id = b.audit_entry_id;
INSERT INTO audit.audit_entry SELECT b.* FROM public.qa_tamper_backup b
WHERE NOT EXISTS (SELECT 1 FROM audit.audit_entry e WHERE e.audit_entry_id = b.audit_entry_id);
DROP TABLE public.qa_tamper_backup;
\echo '== restored - verify must pass again'
