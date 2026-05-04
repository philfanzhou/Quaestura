using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ruoyu.Study.QuestionBank.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_referenced = table.Column<bool>(type: "boolean", nullable: false),
                    subject = table.Column<int>(type: "integer", nullable: true),
                    grade = table.Column<int>(type: "integer", nullable: true),
                    updated_by = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledge_knowledge_parent_id",
                        column: x => x.parent_id,
                        principalTable: "knowledge",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    picture_paths = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    student_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    mistake_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    subject = table.Column<int>(type: "integer", nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "question_content",
                columns: table => new
                {
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    correct_answer = table.Column<string>(type: "text", nullable: true),
                    analysis = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_content", x => x.question_id);
                    table.ForeignKey(
                        name: "FK_question_content_question_question_id",
                        column: x => x.question_id,
                        principalTable: "question",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    weight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_knowledge", x => x.id);
                    table.ForeignKey(
                        name: "FK_question_knowledge_knowledge_knowledge_id",
                        column: x => x.knowledge_id,
                        principalTable: "knowledge",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_question_knowledge_question_question_id",
                        column: x => x.question_id,
                        principalTable: "question",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_parent_id",
                table: "knowledge",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_subject_grade",
                table: "knowledge",
                columns: new[] { "subject", "grade" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_subject_grade_name",
                table: "knowledge",
                columns: new[] { "subject", "grade", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_created_at",
                table: "question",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_question_subject_grade",
                table: "question",
                columns: new[] { "subject", "grade" });

            migrationBuilder.CreateIndex(
                name: "IX_question_subject_grade_level",
                table: "question",
                columns: new[] { "subject", "grade", "level" });

            migrationBuilder.CreateIndex(
                name: "IX_question_subject_grade_type",
                table: "question",
                columns: new[] { "subject", "grade", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_question_user_id",
                table: "question",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_question_content_question_id",
                table: "question_content",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "IX_question_knowledge_knowledge_id",
                table: "question_knowledge",
                column: "knowledge_id");

            migrationBuilder.CreateIndex(
                name: "IX_question_knowledge_question_id_knowledge_id",
                table: "question_knowledge",
                columns: new[] { "question_id", "knowledge_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "question_content");

            migrationBuilder.DropTable(
                name: "question_knowledge");

            migrationBuilder.DropTable(
                name: "knowledge");

            migrationBuilder.DropTable(
                name: "question");
        }
    }
}
