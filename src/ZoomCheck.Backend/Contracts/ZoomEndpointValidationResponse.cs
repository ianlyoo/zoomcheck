namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomEndpointValidationResponse(string PlainToken, string EncryptedToken);
