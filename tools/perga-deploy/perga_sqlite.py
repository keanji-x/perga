"""SQLite schema inspection and durable backup/recovery for stopped Hosts."""
import contextlib
import os
from pathlib import Path
import shutil
import sqlite3
import time
import uuid

from perga_platform import DeployError, sync_dir


def schema_version(db):
    if not Path(db).exists():
        return 0
    with contextlib.closing(sqlite3.connect(Path(db).as_uri() + '?mode=ro', uri=True)) as conn:
        tables = conn.execute("SELECT name FROM sqlite_master WHERE name='schema_migrations'").fetchall()
        if not tables:
            return 0
        return conn.execute('SELECT COALESCE(MAX(version),0) FROM schema_migrations').fetchone()[0]


def validate_database(db):
    with contextlib.closing(sqlite3.connect(Path(db).as_uri() + '?mode=ro', uri=True)) as conn:
        if conn.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
            raise DeployError('SQLite integrity check failed: ' + str(db))


class DatabaseRecovery:
    def backup(self, version):
        if not self.db.exists():
            return
        target = Path(str(self.db) + '.bak-v' + str(version))
        temporary = self.runtime / ('backup-' + uuid.uuid4().hex + '.db')
        try:
            with contextlib.closing(sqlite3.connect(self.db)) as source, contextlib.closing(sqlite3.connect(temporary)) as dest:
                source.backup(dest)
            validate_database(temporary)
            with temporary.open('rb') as stream:
                os.fsync(stream.fileno())
            # Old backup sidecars must never be replayed against new backup bytes.
            for suffix in ('-wal', '-shm'):
                Path(str(target) + suffix).unlink(missing_ok=True)
            os.replace(temporary, target)
            sync_dir(target.parent)
        finally:
            temporary.unlink(missing_ok=True)


    def restore(self, supported, state):
        try:
            required = schema_version(self.db) > supported
        except sqlite3.Error:
            required = True
        if not required and not state.get('restoreRequired'):
            return
        actual_before = state['oldSchemaVersion']
        backup = Path(str(self.db) + '.bak-v' + str(actual_before))
        if not backup.is_file():
            raise DeployError('required schema recovery backup is missing: ' + str(backup))
        validate_database(backup)
        if schema_version(backup) > supported:
            raise DeployError('backup schema exceeds rollback release support')
        state['restoreRequired'] = True
        self.state(state, 'restoring-database')
        failed = str(self.db) + '.failed-' + str(time.time_ns())
        tmp = self.db.parent / ('.restore-' + uuid.uuid4().hex)
        try:
            shutil.copy2(backup, tmp)
            with tmp.open('rb') as stream:
                os.fsync(stream.fileno())
            for suffix in ('', '-wal', '-shm'):
                path = Path(str(self.db) + suffix)
                if path.exists():
                    # Keep the original DB until the restored copy is durable.
                    shutil.copy2(path, failed + suffix)
                    with open(failed + suffix, 'rb') as stream:
                        os.fsync(stream.fileno())
                    if suffix:
                        path.unlink()
            os.replace(tmp, self.db)
            sync_dir(self.db.parent)
            state['restoreRequired'] = False
            self.state(state, 'rolling-back')
        finally:
            tmp.unlink(missing_ok=True)
