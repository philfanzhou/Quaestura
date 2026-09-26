using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quaestura.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deployments started before this migration existed may already have these tables,
            // created by DatabaseInitializer's schema-integrity repair. Leave such tables untouched.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF to_regclass('public.tag') IS NULL THEN
        CREATE TABLE tag (
            id uuid NOT NULL,
            name character varying(100) NOT NULL,
            color character varying(20) NULL,
            description text NULL,
            created_by character varying(36) NULL,
            created_at timestamp with time zone NOT NULL,
            usage_count integer NOT NULL,
            CONSTRAINT ""PK_tag"" PRIMARY KEY (id)
        );
        CREATE UNIQUE INDEX ""IX_tag_name"" ON tag (name);
    END IF;

    IF to_regclass('public.question_tag') IS NULL THEN
        CREATE TABLE question_tag (
            id uuid NOT NULL,
            question_id uuid NOT NULL,
            tag_id uuid NOT NULL,
            created_at timestamp with time zone NOT NULL,
            CONSTRAINT ""PK_question_tag"" PRIMARY KEY (id),
            CONSTRAINT ""FK_question_tag_question_question_id"" FOREIGN KEY (question_id) REFERENCES question (id) ON DELETE CASCADE,
            CONSTRAINT ""FK_question_tag_tag_tag_id"" FOREIGN KEY (tag_id) REFERENCES tag (id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX ""IX_question_tag_question_id_tag_id"" ON question_tag (question_id, tag_id);
        CREATE INDEX ""IX_question_tag_tag_id"" ON question_tag (tag_id);
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "question_tag");

            migrationBuilder.DropTable(
                name: "tag");
        }
    }
}
