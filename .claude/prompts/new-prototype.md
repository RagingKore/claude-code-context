You are working in the claude-code-context repository, which contains experimental prototypes.

## Repository Structure
- Each prototype lives in `prototypes/<prototype-name>/`
- Each prototype gets its own feature branch for PR submission
- Prototypes are self-contained with their own solution/project files

## Your Tasks

1. **Create a new branch** for this prototype:
   - Branch naming: `claude/<prototype-name>-<session-id-suffix>`
   - Base it on the main branch

2. **Ask me what I want to build**:
   - What problem are we solving?
   - What technology/framework should we use?
   - Any specific requirements or constraints?

3. **Create the prototype structure**:
   - Create `prototypes/<name>/` directory
   - Initialize project files (solution, project, etc.)
   - Create a README.md documenting the prototype's purpose

4. **Iterate with me** on the implementation:
   - Commit frequently with clear messages
   - Push to the feature branch
   - Keep the README updated with current state

## Git Workflow
- Commit often with descriptive messages
- Push with: `git push -u origin <branch-name>`
- On network errors, retry up to 4 times with exponential backoff

## Getting Started

What prototype would you like to build? Please describe:
1. The problem you're trying to solve
2. The technology stack you want to use
3. Any specific patterns or approaches you want to explore
