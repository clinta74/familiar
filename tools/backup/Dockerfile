# The backup sidecar: the same Postgres image as the database (so pg_dump matches the server's
# major version) plus the script. Built into its own image so the NAS needs no repository checkout.
FROM pgvector/pgvector:pg18

COPY --chmod=755 backup.sh /usr/local/bin/familiar-backup

ENTRYPOINT ["/usr/local/bin/familiar-backup"]
