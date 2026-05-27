using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyTax.API.Migrations.EF
{
    /// <inheritdoc />
    public partial class AddMlPredictionDatasetPreviewSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PropertyId",
                table: "ml_predictions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.Sql("ALTER TABLE `ml_predictions` ADD COLUMN IF NOT EXISTS `DatasetName` varchar(255) CHARACTER SET utf8mb4 NULL;");
            migrationBuilder.Sql("ALTER TABLE `ml_predictions` ADD COLUMN IF NOT EXISTS `DatasetStoredAs` varchar(255) CHARACTER SET utf8mb4 NULL;");
            migrationBuilder.Sql("ALTER TABLE `ml_predictions` ADD COLUMN IF NOT EXISTS `ExternalPropertyId` varchar(255) CHARACTER SET utf8mb4 NULL;");
            migrationBuilder.Sql("ALTER TABLE `ml_predictions` ADD COLUMN IF NOT EXISTS `OwnerSnapshot` varchar(255) CHARACTER SET utf8mb4 NULL;");
            migrationBuilder.Sql("ALTER TABLE `ml_predictions` ADD COLUMN IF NOT EXISTS `RowNumber` int NULL;");
            migrationBuilder.Sql("ALTER TABLE `ml_predictions` ADD COLUMN IF NOT EXISTS `SourceType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Property';");

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS `IX_ml_predictions_DatasetStoredAs_ModelId_RowNumber` ON `ml_predictions` (`DatasetStoredAs`, `ModelId`, `RowNumber`);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS `IX_ml_predictions_SourceType` ON `ml_predictions` (`SourceType`);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ml_predictions_DatasetStoredAs_ModelId_RowNumber",
                table: "ml_predictions");

            migrationBuilder.DropIndex(
                name: "IX_ml_predictions_SourceType",
                table: "ml_predictions");

            migrationBuilder.DropColumn(
                name: "DatasetName",
                table: "ml_predictions");

            migrationBuilder.DropColumn(
                name: "DatasetStoredAs",
                table: "ml_predictions");

            migrationBuilder.DropColumn(
                name: "ExternalPropertyId",
                table: "ml_predictions");

            migrationBuilder.DropColumn(
                name: "OwnerSnapshot",
                table: "ml_predictions");

            migrationBuilder.DropColumn(
                name: "RowNumber",
                table: "ml_predictions");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "ml_predictions");

            migrationBuilder.AlterColumn<int>(
                name: "PropertyId",
                table: "ml_predictions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
