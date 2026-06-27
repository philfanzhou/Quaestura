# 数据库与存储设计

## 数据库设计

### 单库设计 (ruoyu_study_questionbank)

所有数据存储在单一 SQLite 数据库 (`data/sqlite/ruoyu_study_questionbank.db`)：

```sql
-- knowledge 知识点表
knowledge (
    id              VARCHAR(36) PRIMARY KEY,
    parent_id       VARCHAR(36) REFERENCES knowledge(id),
    name            VARCHAR(255) NOT NULL,          -- 与 subject, grade 构成联合唯一索引
    description     TEXT,
    created_by      VARCHAR(36),
    created_at      TIMESTAMP NOT NULL,
    is_referenced   BOOLEAN NOT NULL,
    subject         INT,
    grade           INT,
    updated_by      VARCHAR(36),
    updated_at      TIMESTAMP
)

-- question 题目表
question (
    id              VARCHAR(36) PRIMARY KEY,
    created_at      TIMESTAMP NOT NULL,
    updated_at      TIMESTAMP NOT NULL,
    level           INT NOT NULL,
    type            INT NOT NULL,
    width           INT NOT NULL,
    height          INT NOT NULL,
    picture_paths   TEXT,             -- SeaweedFS 对象路径，JSON数组
    content         TEXT,
    user_id         VARCHAR(36),
    student_id      VARCHAR(36),
    mistake_id      VARCHAR(36),
    subject         INT NOT NULL,
    grade           INT NOT NULL
)

-- question_knowledge 题目知识点关联表
question_knowledge (
    id              VARCHAR(36) PRIMARY KEY,
    question_id     VARCHAR(36) NOT NULL REFERENCES question(id) ON DELETE CASCADE,
    knowledge_id    VARCHAR(36) NOT NULL REFERENCES knowledge(id) ON DELETE CASCADE,
    weight          REAL NOT NULL DEFAULT 1.0
)
```

## 对象存储 (SeaweedFS)

图片等二进制文件存储在 SeaweedFS (S3 兼容, 端口 8333) 中，数据库只存储对象路径。

### 存储路径格式

```
{year}/{month}/{day}/{guid}/{filename}
```
