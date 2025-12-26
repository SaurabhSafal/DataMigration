using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Configuration;

namespace Helpers
{
    public static class S3bucket
    {
        private static IAmazonS3 s3Client;
        private static string bucketName;
        private static RegionEndpoint bucketRegion;

        // Call once at app startup
        public static void Configure(IConfiguration configuration)
        {
            var awsSection = configuration.GetSection("AWS");

            bucketName = awsSection["BucketName"]!;
            bucketRegion = RegionEndpoint.GetBySystemName(awsSection["Region"]!);

            s3Client = new AmazonS3Client(
                awsSection["AccessKey"],
                awsSection["SecretKey"],
                bucketRegion
            );
        }

        /// <summary>
        /// Uploads file to S3
        /// </summary>
        /// <param name="file">File stream</param>
        /// <param name="folderPath">e.g. "WCL/Logo"</param>
        /// <param name="fileName">e.g. "logo.png"</param>
        /// <returns>Public S3 URL</returns>
        public static string UploadFile(
            Stream file,
            string folderPath,
            string fileName)
        {
            try
            {
                // Build full S3 key
                string keyName = string.IsNullOrWhiteSpace(folderPath)
                    ? fileName
                    : $"{folderPath.TrimEnd('/')}/{fileName}";

                var transferUtility = new TransferUtility(s3Client);
                transferUtility.Upload(file, bucketName, keyName);

                return $"https://{bucketName}.s3.{bucketRegion.SystemName}.amazonaws.com/{keyName}";
            }
            catch (AmazonS3Exception)
            {
                throw;
            }
        }
    }
}
