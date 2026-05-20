using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropertyTax.API.Data;

#nullable disable

namespace PropertyTax.API.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260512090000_AddManualPaymentReferenceFields")]
    public partial class AddManualPaymentReferenceFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Payments",
                type: "varchar(120)",
                maxLength: 120,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceNumber",
                table: "Payments",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ReferenceNumber",
                table: "Payments");
        }
    }
}