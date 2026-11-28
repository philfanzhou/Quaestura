using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quaestura.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveQuestionMistakeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mistake_id",
                table: "question");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mistake_id",
                table: "question",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);
        }
    }
}
