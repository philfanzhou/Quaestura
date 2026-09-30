# Database and Storage Design

## Database design

### Database

The service uses PostgreSQL (Npgsql provider) as its only database. `Program.cs` builds the connection string through `SharedPostgreSqlConnectionStringFactory.BuildOrFallback`: it first composes the production connection string from the Consul shared configuration (`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`), and falls back to the local `ConnectionStrings:Default` when that is not possible.

In production, the PostgreSQL host/port/username/password are injected through the Consul shared configuration; the local `ConnectionStrings:Default` only keeps a password-less dev connection string.

On startup the shared ServiceMantle migration orchestrator applies the EF Core migrations in `src/Database/Migrations/` under a real PostgreSQL session advisory lock scoped to the service id `quaestura`; no manual migration step is required. The lock covers the initial inspection, the verified legacy takeover, the migration execution, and the final inspection, so concurrently starting instances serialize: one executes, the others wait for the lock, re-read the state, and skip. The 30-second lock-acquire budget bounds only waiting for the lock, never the execution. The executor (`QuaesturaMigrationExecutor`) owns the strict inspection/execution contract described in [`docs/development/Deployment.md`](../development/Deployment.md) §2.1; the limited legacy-takeover raw SQL registers only the verified `InitialCreate` baseline.

### Table structure

#### knowledge (knowledge points)

| Column | Type | Constraints | Description |
|------|------|------|------|
| `id` | UUID | PK | Primary key |
| `parent_id` | UUID | FK → knowledge(id), ON DELETE RESTRICT | Parent knowledge point (hierarchy) |
| `name` | VARCHAR(255) | NOT NULL | Knowledge point name |
| `description` | TEXT | NULL | Description |
| `created_by` | VARCHAR(36) | NULL | Creator ID |
| `created_at` | TIMESTAMPTZ | NOT NULL | Creation time |
| `is_referenced` | BOOLEAN | NOT NULL | Whether referenced by any question |
| `subject` | INT | NULL | Subject |
| `grade` | INT | NULL | Grade |
| `updated_by` | VARCHAR(36) | NULL | Last updater ID |
| `updated_at` | TIMESTAMPTZ | NULL | Last update time |

**Indexes**:
- `IX_knowledge_subject_grade_name` UNIQUE (subject, grade, name)
- `IX_knowledge_subject_grade` (subject, grade)
- `IX_knowledge_parent_id` (parent_id)

#### question (questions)

| Column | Type | Constraints | Description |
|------|------|------|------|
| `id` | UUID | PK | Primary key |
| `created_at` | TIMESTAMPTZ | NOT NULL | Creation time |
| `updated_at` | TIMESTAMPTZ | NOT NULL | Last update time (concurrency check) |
| `level` | INT | NOT NULL | Difficulty level |
| `type` | INT | NOT NULL | Question type |
| `width` | INT | NOT NULL | Image width |
| `height` | INT | NOT NULL | Image height |
| `picture_paths` | TEXT | NULL | Image path list (JSON array) |
| `user_id` | VARCHAR(36) | NULL | Uploader ID |
| `student_id` | VARCHAR(36) | NULL | Associated student ID |
| `mistake_id` | VARCHAR(36) | NULL | Associated mistake ID |
| `subject` | INT | NOT NULL | Subject |
| `grade` | INT | NOT NULL | Grade |

**Indexes**:
- `IX_question_subject_grade` (subject, grade)
- `IX_question_subject_grade_level` (subject, grade, level)
- `IX_question_subject_grade_type` (subject, grade, type)
- `IX_question_created_at` (created_at)
- `IX_question_user_id` (user_id)

#### question_content (question body)

| Column | Type | Constraints | Description |
|------|------|------|------|
| `question_id` | UUID | PK, FK → question(id) ON DELETE CASCADE | Associated question |
| `content` | TEXT | NULL | Question content |
| `correct_answer` | TEXT | NULL | Correct answer |
| `analysis` | TEXT | NULL | Explanation |

#### question_knowledge (question–knowledge point association)

| Column | Type | Constraints | Description |
|------|------|------|------|
| `id` | UUID | PK | Primary key |
| `question_id` | UUID | NOT NULL, FK → question(id) ON DELETE CASCADE | Question |
| `knowledge_id` | UUID | NOT NULL, FK → knowledge(id) ON DELETE CASCADE | Knowledge point |
| `weight` | DOUBLE PRECISION | NOT NULL DEFAULT 1.0 | Weight |

**Indexes**:
- `IX_question_knowledge_question_id_knowledge_id` UNIQUE (question_id, knowledge_id)
- `IX_question_knowledge_knowledge_id` (knowledge_id)

#### tag (tags)

| Column | Type | Constraints | Description |
|------|------|------|------|
| `id` | UUID | PK | Primary key |
| `name` | VARCHAR(100) | NOT NULL, UNIQUE | Tag name (unique, case-insensitive) |
| `color` | VARCHAR(20) | NULL | Display color (HEX format, such as `#FF6B6B`) |
| `description` | TEXT | NULL | Description |
| `created_by` | VARCHAR(36) | NULL | Creator ID (written from the JWT `sub` claim; used for ownership checks) |
| `created_at` | TIMESTAMPTZ | NOT NULL | Creation time |
| `usage_count` | INT | NOT NULL DEFAULT 0 | Reference count (used for popularity sorting and pre-delete checks) |

**Indexes**:
- `IX_tag_name` UNIQUE (name) — a plain, case-sensitive unique index; case-insensitive uniqueness is enforced by the application-level check in `TagService`

**Design notes**:
- Parallel to `knowledge` (not tied to subject/grade), so tags can be reused across subjects and grades
- No hierarchy (no parent_id)
- `usage_count` is maintained automatically as `question_tag` rows are added/removed; deletion requires `usage_count == 0`
- `created_by` is written by the server from the JWT and cannot be forged by clients; updates/deletes require `created_by == current userId` (strict ownership, see [Authentication.md](../development/Authentication.md))

#### question_tag (question–tag association)

| Column | Type | Constraints | Description |
|------|------|------|------|
| `id` | UUID | PK | Primary key |
| `question_id` | UUID | NOT NULL, FK → question(id) ON DELETE CASCADE | Question |
| `tag_id` | UUID | NOT NULL, FK → tag(id) ON DELETE CASCADE | Tag |
| `created_at` | TIMESTAMPTZ | NOT NULL | Association creation time |

**Indexes**:
- `IX_question_tag_question_id_tag_id` UNIQUE (question_id, tag_id)
- `IX_question_tag_tag_id` (tag_id)

### ER diagram

```
┌────────────────┐
│   knowledge    │
│                │◄─────┐
│ id (PK)        │      │
│ parent_id (FK) │──┐   │ (self-reference, ON DELETE RESTRICT)
│ name           │  │   │
│ subject, grade │  │   │
│ is_referenced  │  │   │
└────────────────┘  │   │
                    │   │
                    │   │
                    │   │ ON DELETE CASCADE
                    │   │
                    │   │      ┌──────────────────────┐
                    │   │      │ question_knowledge   │
                    │   │      │                      │
                    │   └──────┤ knowledge_id (FK)    │
                    │          │ question_id (FK)     │
                    │          │ weight               │
                    │          └──────────────────────┘
                    │                    │
                    │                    │
                    │              ┌─────▼──────┐
                    │              │  question  │
                    │              │            │
                    │              │ id (PK)    │◄──┐
                    │              │ subject    │   │
                    │              │ grade      │   │ 1:1
                    │              │ level/type │   │
                    │              └────────────┘   │
                    │                               │
                    │              ┌────────────────▼─────┐
                    │              │ question_content      │
                    │              │                       │
                    └──────────────┤ question_id (PK, FK)  │
                                   │ content               │
                                   │ correct_answer        │
                                   │ analysis              │
                                   └───────────────────────┘

┌────────────────┐
│      tag       │
│                │◄─────┐
│ id (PK)        │      │
│ name (UNIQUE)  │      │ ON DELETE CASCADE
│ color          │      │
│ description    │      │
│ usage_count    │      │
└────────────────┘      │
                        │      ┌──────────────────────┐
                        │      │    question_tag      │
                        │      │                      │
                        └──────┤ tag_id (FK)          │
                               │ question_id (FK)     │
                               │ created_at           │
                               └──────────────────────┘
                                        │
                                        │ 0..N
                                        ▼
                                   ┌─────────┐
                                   │ question│ (same as above)
                                   └─────────┘
```

## Object storage (SeaweedFS)

Images and other binary files are stored in SeaweedFS (S3-compatible, port 8333); the database only stores object paths.

### Storage path format

```
{year}/{month}/{day}/{guid}/{filename}
```

Generated automatically by `IOssService.UploadAsync`; callers do not need to handle it.

### Bucket namespace

The `OssBucket.Questions` constant isolates question-bank images from other services' object storage.
