using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyTax.API.Migrations
{
    /// <inheritdoc />
    public partial class RenameTaxpayersToPropertyOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Taxpayers_TaxpayerId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Properties_Taxpayers_TaxpayerId",
                table: "Properties");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Taxpayers",
                table: "Taxpayers");

            migrationBuilder.RenameTable(
                name: "Taxpayers",
                newName: "property_owners");

            migrationBuilder.AddPrimaryKey(
                name: "PK_property_owners",
                table: "property_owners",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_property_owners_TaxpayerId",
                table: "Payments",
                column: "TaxpayerId",
                principalTable: "property_owners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_property_owners_TaxpayerId",
                table: "Properties",
                column: "TaxpayerId",
                principalTable: "property_owners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_property_owners_TaxpayerId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Properties_property_owners_TaxpayerId",
                table: "Properties");

            migrationBuilder.DropPrimaryKey(
                name: "PK_property_owners",
                table: "property_owners");

            migrationBuilder.RenameTable(
                name: "property_owners",
                newName: "Taxpayers");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Taxpayers",
                table: "Taxpayers",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Taxpayers_TaxpayerId",
                table: "Payments",
                column: "TaxpayerId",
                principalTable: "Taxpayers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_Taxpayers_TaxpayerId",
                table: "Properties",
                column: "TaxpayerId",
                principalTable: "Taxpayers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
