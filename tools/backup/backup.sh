#!/bin/sh
# Nightly pg_dump that proves itself: each dump is restored into a scratch database and checked
# for every live table. A dump that won't restore is deleted, because a file that looks like a
# backup and isn't one is worse than no file.
#
#   familiar-backup            # loop: run daily at BACKUP_AT_HOUR (UTC)
#   familiar-backup --once     # one backup now, then exit
#
# By hand on the NAS:
#   docker exec familiar-backup familiar-backup --once

set -eu

PGHOST="${PGHOST:-postgres}"
PGUSER="${PGUSER:-familiar}"
PGDATABASE="${PGDATABASE:-familiar}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_KEEP="${BACKUP_KEEP:-30}"
BACKUP_AT_HOUR="${BACKUP_AT_HOUR:-3}"
VERIFY_DB="${VERIFY_DB:-familiar_verify}"

export PGHOST PGUSER

# Only files this script wrote are pruned - never a dump somebody took by hand.
DUMP_GLOB='familiar-????-??-??T??????Z.dump'

log() { echo "[backup $(date -u '+%Y-%m-%dT%H:%M:%SZ')] $*"; }

# Exact row counts for every table in public, discovered from the catalogue so a new table can't
# be silently left out of the comparison.
row_counts() {
    psql -d "$1" -At -c "
        select c.relname || '=' || (xpath('/row/c/text()',
            query_to_xml(format('select count(*) as c from %I.%I', n.nspname, c.relname), false, true, '')))[1]::text
        from pg_class c join pg_namespace n on n.oid = c.relnamespace
        where c.relkind = 'r' and n.nspname = 'public'
        order by c.relname;"
}

# Refuse to write into the container's own layer, where a "backup" vanishes on recreate. A mounted
# volume is on a different device from /.
check_mount() {
    if [ "$(stat -c %d "$BACKUP_DIR")" = "$(stat -c %d /)" ]; then
        log "ERROR: $BACKUP_DIR is not a mounted volume; refusing to write backups into the container"
        exit 1
    fi
}

backup_once() {
    check_mount

    stamp=$(date -u '+%Y-%m-%dT%H%M%SZ')
    file="$BACKUP_DIR/familiar-$stamp.dump"
    partial="$file.partial"

    log "dumping $PGDATABASE"
    pg_dump -d "$PGDATABASE" --format=custom --file="$partial"

    log "verifying by restore into $VERIFY_DB"
    dropdb --if-exists "$VERIFY_DB"
    createdb "$VERIFY_DB"
    if ! pg_restore --no-owner --exit-on-error -d "$VERIFY_DB" "$partial"; then
        log "ERROR: dump did not restore; deleting it"
        rm -f "$partial"
        dropdb --if-exists "$VERIFY_DB"
        return 1
    fi

    source_counts=$(row_counts "$PGDATABASE")
    restored_counts=$(row_counts "$VERIFY_DB")
    dropdb --if-exists "$VERIFY_DB"

    # Row counts can't be compared for equality: the live database keeps taking writes after the
    # dump. So the check is that every live table exists in the restore, and the restored counts
    # are logged for a human to eyeball.
    echo "$source_counts" | while IFS='=' read -r table count; do
        restored=$(echo "$restored_counts" | awk -F= -v t="$table" '$1 == t { print $2 }')
        if [ -z "$restored" ]; then
            log "ERROR: table $table missing from the restore"
            exit 1
        fi
    done || { rm -f "$partial"; return 1; }

    mv "$partial" "$file"
    log "ok: $file ($(du -h "$file" | cut -f1)); tables: $(echo "$restored_counts" | tr '\n' ' ')"

    # Retention: newest BACKUP_KEEP of our own dumps.
    # shellcheck disable=SC2012
    ls -1t "$BACKUP_DIR"/$DUMP_GLOB 2>/dev/null | tail -n +"$((BACKUP_KEEP + 1))" | while read -r old; do
        log "pruning $old"
        rm -f "$old"
    done
}

seconds_until_next_run() {
    now=$(date -u +%s)
    today=$(date -u -d "$(date -u +%Y-%m-%d) $BACKUP_AT_HOUR:00:00" +%s)
    if [ "$today" -gt "$now" ]; then
        echo $((today - now))
    else
        echo $((today + 86400 - now))
    fi
}

if [ "${1:-}" = "--once" ]; then
    backup_once
    exit $?
fi

log "scheduled daily at ${BACKUP_AT_HOUR}:00 UTC, keeping $BACKUP_KEEP"
while true; do
    wait=$(seconds_until_next_run)
    log "next backup in ${wait}s"
    sleep "$wait"
    backup_once || log "backup failed; will try again at the next scheduled time"
done
