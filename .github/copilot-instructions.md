# Copilot Instructions for HHS Call Center Demo

## Project Overview
This is an HHS (Health and Human Services) Call Center solution that:
- Transcribes audio recordings into text
- Uses AI to automatically analyze transcripts
- Asks a series of questions about the transcript
- Records and stores the results

## Technology Stack
- **Language**: Primarily .NET/C# (based on .gitignore configuration)
- **IDE**: Visual Studio
- **Platform**: Windows-based development environment

## Code Style and Conventions
- Follow standard C# naming conventions
- Use PascalCase for public members and classes
- Use camelCase for private fields and local variables
- Keep code clean and well-documented

## Project Structure
The repository follows a standard Visual Studio solution structure:
- Build artifacts should go in `bin/` and `obj/` directories (gitignored)
- Sensitive information (connection strings, API keys) should be stored in environment variables or `.env` files (gitignored)

## Important Notes
- **Security**: Never hardcode credentials, API keys, or sensitive information
- **Environment Variables**: Use `.env` files for local configuration (already in .gitignore)
- **Audio Processing**: This project deals with audio transcription and AI analysis
- **Healthcare Context**: Keep in mind this is for HHS/healthcare applications - be mindful of HIPAA and data privacy considerations

## Development Guidelines
- Test audio transcription features thoroughly
- Ensure AI question generation and analysis is accurate and relevant
- Handle edge cases in audio processing (poor quality, multiple speakers, etc.)
- Maintain clear separation between transcription, analysis, and storage layers

## Dependencies
- Azure services may be used for AI/speech services
- Ensure all NuGet packages are restored before building
- Check that necessary Azure resources are configured

## When Making Changes
- Ensure changes don't break the audio-to-text pipeline
- Test the AI question generation thoroughly
- Validate that results are being recorded correctly
- Consider performance implications for processing large audio files
