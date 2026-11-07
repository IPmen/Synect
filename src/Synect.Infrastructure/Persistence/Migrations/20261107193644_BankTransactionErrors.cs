using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankTransactionErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "error_code",
                table: "bank_transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_message",
                table: "bank_transactions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_error",
                table: "bank_transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "error_code",
                table: "bank_transactions");

            migrationBuilder.DropColumn(
                name: "error_message",
                table: "bank_transactions");

            migrationBuilder.DropColumn(
                name: "has_error",
                table: "bank_transactions");
        }
    }
}
