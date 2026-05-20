using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyTax.API.Migrations
{
    /// <inheritdoc />
    public partial class AddRegionXiPropertyRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BarangayId",
                table: "Properties",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "Properties",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TaxDeclarationNumber",
                table: "Properties",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ZoningClassification",
                table: "Properties",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "provinces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RegionCode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PsgcCode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provinces", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "cities_municipalities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProvinceId = table.Column<int>(type: "int", nullable: false),
                    PsgcCode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LguType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cities_municipalities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cities_municipalities_provinces_ProvinceId",
                        column: x => x.ProvinceId,
                        principalTable: "provinces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "barangays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CityMunicipalityId = table.Column<int>(type: "int", nullable: false),
                    PsgcCode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_barangays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_barangays_cities_municipalities_CityMunicipalityId",
                        column: x => x.CityMunicipalityId,
                        principalTable: "cities_municipalities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_BarangayId",
                table: "Properties",
                column: "BarangayId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_TaxDeclarationNumber",
                table: "Properties",
                column: "TaxDeclarationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_barangays_CityMunicipalityId_Name",
                table: "barangays",
                columns: new[] { "CityMunicipalityId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_barangays_PsgcCode",
                table: "barangays",
                column: "PsgcCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cities_municipalities_ProvinceId_Name",
                table: "cities_municipalities",
                columns: new[] { "ProvinceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_cities_municipalities_PsgcCode",
                table: "cities_municipalities",
                column: "PsgcCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provinces_PsgcCode",
                table: "provinces",
                column: "PsgcCode",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_barangays_BarangayId",
                table: "Properties",
                column: "BarangayId",
                principalTable: "barangays",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Properties_barangays_BarangayId",
                table: "Properties");

            migrationBuilder.DropTable(
                name: "barangays");

            migrationBuilder.DropTable(
                name: "cities_municipalities");

            migrationBuilder.DropTable(
                name: "provinces");

            migrationBuilder.DropIndex(
                name: "IX_Properties_BarangayId",
                table: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Properties_TaxDeclarationNumber",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "BarangayId",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "TaxDeclarationNumber",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ZoningClassification",
                table: "Properties");
        }
    }
}
