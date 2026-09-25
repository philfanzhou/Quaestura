using System.Threading.Tasks;
using Quaestura.Common.Database;

namespace Quaestura.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(QuaesturaDbContext context, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        await Common.Database.DatabaseInitializer.InitializeAsync(context, loggerFactory, GetTableCreationSql);
    }

    private static string? GetTableCreationSql(string tableName)
    {
        return tableName switch
        {
            "knowledge" => @"
                CREATE TABLE IF NOT EXISTS knowledge (
                    id uuid NOT NULL,
                    parent_id uuid NULL,
                    name character varying(255) NOT NULL,
                    description text NULL,
                    created_by character varying(36) NULL,
                    created_at timestamp with time zone NOT NULL,
                    is_referenced boolean NOT NULL,
                    subject integer NULL,
                    grade integer NULL,
                    updated_by character varying(36) NULL,
                    updated_at timestamp with time zone NULL,
                    CONSTRAINT PK_knowledge PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_knowledge_subject_grade_name ON knowledge (subject, grade, name);
                CREATE INDEX IF NOT EXISTS IX_knowledge_subject_grade ON knowledge (subject, grade);
                CREATE INDEX IF NOT EXISTS IX_knowledge_parent_id ON knowledge (parent_id);",

            "question" => @"
                CREATE TABLE IF NOT EXISTS question (
                    id uuid NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    level integer NOT NULL,
                    type integer NOT NULL,
                    width integer NOT NULL,
                    height integer NOT NULL,
                    picture_paths text NULL,
                    user_id character varying(36) NULL,
                    student_id character varying(36) NULL,
                    mistake_id character varying(36) NULL,
                    subject integer NOT NULL,
                    grade integer NOT NULL,
                    CONSTRAINT PK_question PRIMARY KEY (id)
                );
                CREATE INDEX IF NOT EXISTS IX_question_subject_grade ON question (subject, grade);
                CREATE INDEX IF NOT EXISTS IX_question_subject_grade_level ON question (subject, grade, level);
                CREATE INDEX IF NOT EXISTS IX_question_subject_grade_type ON question (subject, grade, type);
                CREATE INDEX IF NOT EXISTS IX_question_created_at ON question (created_at);
                CREATE INDEX IF NOT EXISTS IX_question_user_id ON question (user_id);",

            "question_content" => @"
                CREATE TABLE IF NOT EXISTS question_content (
                    question_id uuid NOT NULL,
                    content text NULL,
                    correct_answer text NULL,
                    analysis text NULL,
                    CONSTRAINT PK_question_content PRIMARY KEY (question_id),
                    CONSTRAINT FK_question_content_question_question_id 
                        FOREIGN KEY (question_id) REFERENCES question(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_question_content_question_id ON question_content (question_id);",

            "question_knowledge" => @"
                CREATE TABLE IF NOT EXISTS question_knowledge (
                    id uuid NOT NULL,
                    question_id uuid NOT NULL,
                    knowledge_id uuid NOT NULL,
                    weight double precision NOT NULL,
                    CONSTRAINT PK_question_knowledge PRIMARY KEY (id),
                    CONSTRAINT FK_question_knowledge_question_question_id
                        FOREIGN KEY (question_id) REFERENCES question(id) ON DELETE CASCADE,
                    CONSTRAINT FK_question_knowledge_knowledge_knowledge_id
                        FOREIGN KEY (knowledge_id) REFERENCES knowledge(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_question_knowledge_question_id_knowledge_id ON question_knowledge (question_id, knowledge_id);
                CREATE INDEX IF NOT EXISTS IX_question_knowledge_knowledge_id ON question_knowledge (knowledge_id);",

            "tag" => @"
                CREATE TABLE IF NOT EXISTS tag (
                    id uuid NOT NULL,
                    name character varying(100) NOT NULL,
                    color character varying(20) NULL,
                    description text NULL,
                    created_by character varying(36) NULL,
                    created_at timestamp with time zone NOT NULL,
                    usage_count integer NOT NULL DEFAULT 0,
                    CONSTRAINT PK_tag PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_tag_name ON tag (name);",

            "question_tag" => @"
                CREATE TABLE IF NOT EXISTS question_tag (
                    id uuid NOT NULL,
                    question_id uuid NOT NULL,
                    tag_id uuid NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT PK_question_tag PRIMARY KEY (id),
                    CONSTRAINT FK_question_tag_question_question_id
                        FOREIGN KEY (question_id) REFERENCES question(id) ON DELETE CASCADE,
                    CONSTRAINT FK_question_tag_tag_tag_id
                        FOREIGN KEY (tag_id) REFERENCES tag(id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_question_tag_question_id_tag_id ON question_tag (question_id, tag_id);
                CREATE INDEX IF NOT EXISTS IX_question_tag_tag_id ON question_tag (tag_id);",

            _ => null
        };
    }
}
