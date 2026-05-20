using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropertyTax.API.Migrations.EF
{
    /// <inheritdoc />
    public partial class ClearMLAndAddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Clear all ML data for all users
            migrationBuilder.Sql("DELETE FROM `ml_predictions`;");
            migrationBuilder.Sql("DELETE FROM `ml_training_jobs`;");
            migrationBuilder.Sql("DELETE FROM `ml_models`;");

            // Step 2: Conditionally drop FK if it exists
            migrationBuilder.Sql(@"SET @fk = (
    SELECT CONSTRAINT_NAME
    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
    WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'ml_models'
        AND COLUMN_NAME = 'CreatedById'
        AND REFERENCED_TABLE_NAME = 'Users'
    LIMIT 1
);
IF @fk IS NOT NULL THEN
    SET @s = CONCAT('ALTER TABLE `ml_models` DROP FOREIGN KEY `', @fk, '`');
    PREPARE stmt FROM @s;
    EXECUTE stmt;
    DEALLOCATE PREPARE stmt;
END IF;");

            migrationBuilder.AlterColumn<string>(
                name: "Logs",
                table: "ml_training_jobs",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<decimal>(
                name: "Probability",
                table: "ml_predictions",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "ml_models",
                type: "tinyint(1)",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)",
                oldDefaultValue: false);

            // Step 3: Create data_notifications table safely with IF NOT EXISTS
            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS `data_notifications` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `UserId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
  `Title` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
  `Message` longtext CHARACTER SET utf8mb4 NOT NULL,
  `Type` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
  `EntityName` varchar(100) CHARACTER SET utf8mb4 DEFAULT NULL,
  `EntityId` varchar(100) CHARACTER SET utf8mb4 DEFAULT NULL,
  `IsRead` tinyint(1) NOT NULL,
  `CreatedAtUtc` datetime(6) NOT NULL,
  `ReadAtUtc` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`Id`),
  CONSTRAINT `FK_data_notifications_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE,
  INDEX `IX_data_notifications_CreatedAtUtc` (`CreatedAtUtc`),
  INDEX `IX_data_notifications_UserId_IsRead_CreatedAtUtc` (`UserId`, `IsRead`, `CreatedAtUtc`)
) CHARACTER SET = utf8mb4;");

            // Step 4: Conditionally add FK if it does not exist
            migrationBuilder.Sql(@"SET @exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
    WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'ml_models'
        AND COLUMN_NAME = 'CreatedById'
        AND REFERENCED_TABLE_NAME = 'Users'
);
IF @exists = 0 THEN
    ALTER TABLE `ml_models`
        ADD CONSTRAINT `FK_ml_models_Users_CreatedById` FOREIGN KEY (`CreatedById`) REFERENCES `Users`(`Id`);
END IF;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Conditionally drop FK if it exists
            migrationBuilder.Sql(@"SET @fk = (
    SELECT CONSTRAINT_NAME
    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
    WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'ml_models'
        AND COLUMN_NAME = 'CreatedById'
        AND REFERENCED_TABLE_NAME = 'Users'
    LIMIT 1
);
IF @fk IS NOT NULL THEN
    SET @s = CONCAT('ALTER TABLE `ml_models` DROP FOREIGN KEY `', @fk, '`');
    PREPARE stmt FROM @s;
    EXECUTE stmt;
    DEALLOCATE PREPARE stmt;
END IF;");

            // Drop data_notifications table if it exists
            migrationBuilder.Sql("DROP TABLE IF EXISTS `data_notifications`;");

            migrationBuilder.AlterColumn<string>(
                name: "Logs",
                table: "ml_training_jobs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<decimal>(
                name: "Probability",
                table: "ml_predictions",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(10,4)",
                oldPrecision: 10,
                oldScale: 4);

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "ml_models",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "tinyint(1)");

            // Conditionally add FK if it does not exist
            migrationBuilder.Sql(@"SET @exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
    WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'ml_models'
        AND COLUMN_NAME = 'CreatedById'
        AND REFERENCED_TABLE_NAME = 'Users'
);
IF @exists = 0 THEN
    ALTER TABLE `ml_models`
        ADD CONSTRAINT `FK_ml_models_Users_CreatedById` FOREIGN KEY (`CreatedById`) REFERENCES `Users`(`Id`) ON DELETE SET NULL;
END IF;");
        }
    }
}
