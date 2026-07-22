# 数据库与存储设计

## 数据库设计

### 数据库

本服务使用 PostgreSQL（Npgsql 提供程序）作为唯一数据库，由 `Program.cs` 通过 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 构建连接串：优先从 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）合成生产连接串，无法合成时回退到本地 `ConnectionStrings:Default`。

生产环境的 PostgreSQL 主机/端口/账号/密码由 Consul 共享配置注入；本地 `ConnectionStrings:Default` 仅保留无密码的 dev 连接串。

启动时 `DatabaseInitializer.InitializeAsync` 自动建表（`CREATE TABLE IF NOT EXISTS`），无需手动执行迁移。

迁移文件位于 `src/Database/Migrations/`，仅供 EF Core 工具使用，运行时通过 `DatabaseInitializer` 的原生 SQL 初始化。

### 表结构

#### knowledge 知识点表

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `parent_id` | UUID | FK → knowledge(id), ON DELETE RESTRICT | 父知识点（层级结构） |
| `name` | VARCHAR(255) | NOT NULL | 知识点名称 |
| `description` | TEXT | NULL | 描述 |
| `created_by` | VARCHAR(36) | NULL | 创建者 ID |
| `created_at` | TIMESTAMPTZ | NOT NULL | 创建时间 |
| `is_referenced` | BOOLEAN | NOT NULL | 是否被题目引用 |
| `subject` | INT | NULL | 学科 |
| `grade` | INT | NULL | 年级 |
| `updated_by` | VARCHAR(36) | NULL | 更新者 ID |
| `updated_at` | TIMESTAMPTZ | NULL | 更新时间 |

**索引**：
- `IX_knowledge_subject_grade_name` UNIQUE (subject, grade, name)
- `IX_knowledge_subject_grade` (subject, grade)
- `IX_knowledge_parent_id` (parent_id)

#### question 题目表

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `created_at` | TIMESTAMPTZ | NOT NULL | 创建时间 |
| `updated_at` | TIMESTAMPTZ | NOT NULL | 更新时间（并发检查） |
| `level` | INT | NOT NULL | 难度等级 |
| `type` | INT | NOT NULL | 题目类型 |
| `width` | INT | NOT NULL | 图片宽度 |
| `height` | INT | NOT NULL | 图片高度 |
| `picture_paths` | TEXT | NULL | 图片路径列表（JSON 数组） |
| `user_id` | VARCHAR(36) | NULL | 上传者 ID |
| `student_id` | VARCHAR(36) | NULL | 关联学生 ID |
| `mistake_id` | VARCHAR(36) | NULL | 关联错题 ID |
| `subject` | INT | NOT NULL | 学科 |
| `grade` | INT | NOT NULL | 年级 |

**索引**：
- `IX_question_subject_grade` (subject, grade)
- `IX_question_subject_grade_level` (subject, grade, level)
- `IX_question_subject_grade_type` (subject, grade, type)
- `IX_question_created_at` (created_at)
- `IX_question_user_id` (user_id)

#### question_content 题目正文表

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `question_id` | UUID | PK, FK → question(id) ON DELETE CASCADE | 关联题目 |
| `content` | TEXT | NULL | 题目内容 |
| `correct_answer` | TEXT | NULL | 正确答案 |
| `analysis` | TEXT | NULL | 解析 |

#### question_knowledge 题目知识点关联表

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `question_id` | UUID | NOT NULL, FK → question(id) ON DELETE CASCADE | 题目 |
| `knowledge_id` | UUID | NOT NULL, FK → knowledge(id) ON DELETE CASCADE | 知识点 |
| `weight` | DOUBLE PRECISION | NOT NULL DEFAULT 1.0 | 权重 |

**索引**：
- `IX_question_knowledge_question_id_knowledge_id` UNIQUE (question_id, knowledge_id)
- `IX_question_knowledge_knowledge_id` (knowledge_id)

#### tag 标签表

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `name` | VARCHAR(100) | NOT NULL, UNIQUE | 标签名（不区分大小写唯一） |
| `color` | VARCHAR(20) | NULL | 显示颜色（HEX 格式，如 `#FF6B6B`） |
| `description` | TEXT | NULL | 描述 |
| `created_by` | VARCHAR(36) | NULL | 创建者 ID（从 JWT `sub` claim 写入，用于归属校验） |
| `created_at` | TIMESTAMPTZ | NOT NULL | 创建时间 |
| `usage_count` | INT | NOT NULL DEFAULT 0 | 引用次数（用于按热度排序、删除前检查） |

**索引**：
- `IX_tag_name` UNIQUE (LOWER(name)) — 不区分大小写唯一，通过 PostgreSQL 函数索引实现

**设计说明**：
- 与 `knowledge` 平行（不与 subject/grade 强绑定），便于跨学科年级复用
- 无层级（无 parent_id）
- `usage_count` 由 `question_tag` 增/减自动维护，删除前检查 `usage_count == 0`
- `created_by` 由服务端从 JWT 写入，客户端无法伪造；修改/删除时校验 `created_by == 当前 userId`（严格归属制，详见 [Authentication.md](../development/Authentication.md)）

#### question_tag 题目标签关联表

| 字段 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `id` | UUID | PK | 主键 |
| `question_id` | UUID | NOT NULL, FK → question(id) ON DELETE CASCADE | 题目 |
| `tag_id` | UUID | NOT NULL, FK → tag(id) ON DELETE CASCADE | 标签 |
| `created_at` | TIMESTAMPTZ | NOT NULL | 关联创建时间 |

**索引**：
- `IX_question_tag_question_id_tag_id` UNIQUE (question_id, tag_id)
- `IX_question_tag_tag_id` (tag_id)

### ER 图

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
                                   │ question│ (复用上方)
                                   └─────────┘
```

## 对象存储 (SeaweedFS)

图片等二进制文件存储在 SeaweedFS（S3 兼容，端口 8333）中，数据库只存储对象路径。

### 存储路径格式

```
{year}/{month}/{day}/{guid}/{filename}
```

由 `IOssService.UploadAsync` 自动生成，调用方无需关心。

### Bucket 命名空间

通过 `OssBucket.Questions` 常量隔离题库图片与其他服务的对象存储。
