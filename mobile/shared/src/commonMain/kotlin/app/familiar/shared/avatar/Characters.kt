// Generated from client/src/avatar/characters.json by client/scripts/protocol.mjs - don't edit.
// To change it, change the JSON (the web client reads it too), then npm run protocol.

package app.familiar.shared.avatar

import app.familiar.shared.protocol.LlamaColours

/** A built-in character, as the studio offers it. The first preset is its default. */
data class Character(
    val id: String,
    val name: String,
    val blurb: String,
    val has3d: Boolean,
    /** What it can wear; "none" first. */
    val accessories: List<String>,
    /** What each colour paints on it, in order; a colour it doesn't use isn't here. */
    val colourLabels: Map<String, String>,
    val presets: List<Preset>,
)

data class Preset(val name: String, val colours: LlamaColours)

/** The built-in characters, in the studio's order. */
val characters: List<Character> = listOf(
    Character(
        id = "llama",
        name = "Llama",
        blurb = "Big ears that show every feeling, and spectacular lashes.",
        has3d = true,
        accessories = listOf("none", "bow", "scarf", "glasses", "flower"),
        colourLabels = linkedMapOf("wool" to "Fleece", "face" to "Face", "ear" to "Ears & hooves", "blush" to "Cheeks", "accent" to "Accessory"),
        presets = listOf(
            Preset("Classic", LlamaColours("#fdfaf5", "#e4d6cc", "#b3a093", "#f4aab5", "#7c5cff")),
            Preset("Cocoa", LlamaColours("#8a6249", "#ead2bd", "#5c3f2e", "#f2a3a3", "#e8b04a")),
            Preset("Storm", LlamaColours("#b9bcc4", "#ece6e1", "#6f7380", "#f0a6b8", "#3d7bd9")),
            Preset("Midnight", LlamaColours("#3b3a48", "#d9cfc8", "#22212b", "#e891a6", "#f5c542")),
            Preset("Rosy", LlamaColours("#f7d4dc", "#f3e2d9", "#d78ea0", "#f08aa5", "#8d5bd6")),
            Preset("Mint", LlamaColours("#d3efe3", "#efe4da", "#7fbfa5", "#f4a9b4", "#e86f5a")),
        ),
    ),
    Character(
        id = "fox",
        name = "Fox",
        blurb = "Swivelling pointed ears and a bushy tail that swishes when it’s happy.",
        has3d = true,
        accessories = listOf("none", "bow", "scarf", "glasses", "flower"),
        colourLabels = linkedMapOf("wool" to "Coat", "face" to "Muzzle & chest", "ear" to "Inner ears & paws", "blush" to "Cheeks", "accent" to "Accessory"),
        presets = listOf(
            Preset("Red fox", LlamaColours("#e0773a", "#fbf1e4", "#3a2721", "#f4a0a0", "#2e7d8c")),
            Preset("Arctic", LlamaColours("#eef1f5", "#ffffff", "#7d8696", "#f2a6b8", "#3d7bd9")),
            Preset("Silver", LlamaColours("#6f7280", "#e8e6e3", "#2b2c33", "#eb98ad", "#e8b04a")),
            Preset("Fennec", LlamaColours("#e9c99b", "#fbf3e6", "#c98f6b", "#f4a9a0", "#d6577c")),
        ),
    ),
    Character(
        id = "hedgehog",
        name = "Hedgehog",
        blurb = "A round ball of soft prickles that bristle when it’s startled and lie flat when it’s sad.",
        has3d = true,
        accessories = listOf("none", "bow", "scarf", "glasses", "flower"),
        colourLabels = linkedMapOf("wool" to "Prickles", "face" to "Face & tummy", "ear" to "Ears & paws", "blush" to "Cheeks", "accent" to "Accessory"),
        presets = listOf(
            Preset("Hedgerow", LlamaColours("#a38a74", "#f6ecdf", "#d9b598", "#f2a3a3", "#5d9a4a")),
            Preset("Snowy", LlamaColours("#d8d2c8", "#fbf6ef", "#e3c5c2", "#f4a9b8", "#8d5bd6")),
            Preset("Cinnamon", LlamaColours("#b06a3b", "#f6e2cc", "#d99c7a", "#f19a8e", "#2e7d8c")),
        ),
    ),
    Character(
        id = "monster",
        name = "Fuzzy monster",
        blurb = "Googly eyes, stubby horns and fur that stands on end when it’s surprised.",
        has3d = true,
        accessories = listOf("none", "bow", "scarf", "glasses", "flower"),
        colourLabels = linkedMapOf("wool" to "Fur", "face" to "Belly", "ear" to "Horns & paws", "blush" to "Cheeks", "accent" to "Accessory"),
        presets = listOf(
            Preset("Teal", LlamaColours("#3bb3a8", "#a8e6d8", "#f4d35e", "#f58f9e", "#e8505b")),
            Preset("Grape", LlamaColours("#7b52c7", "#c9b4ef", "#ffd166", "#ff8fab", "#06d6a0")),
            Preset("Tangerine", LlamaColours("#f08a36", "#ffd2a8", "#6c4ab6", "#ef6f8a", "#118ab2")),
            Preset("Bubblegum", LlamaColours("#f28bbd", "#ffd0e6", "#8ecae6", "#e5527f", "#ffb703")),
        ),
    ),
    Character(
        id = "otter",
        name = "Otter",
        blurb = "Sleek and round, with a big whiskery muzzle and a thick tail curled round beside it.",
        has3d = true,
        accessories = listOf("none", "bow", "scarf", "glasses", "flower"),
        colourLabels = linkedMapOf("wool" to "Coat", "face" to "Muzzle & chest", "ear" to "Nose & paws", "blush" to "Cheeks", "accent" to "Accessory"),
        presets = listOf(
            Preset("River", LlamaColours("#8a5a3c", "#ecd9c0", "#3f2a1f", "#f2a3a3", "#2e7d8c")),
            Preset("Sea", LlamaColours("#6b5a4e", "#f1e6d6", "#2f2620", "#f0a6b8", "#e8b04a")),
            Preset("Golden", LlamaColours("#b37b46", "#f5e4c8", "#5a3a22", "#f4a9a0", "#5d9a4a")),
            Preset("Snow", LlamaColours("#cfc6bc", "#fbf7f1", "#6f6359", "#f4a9b8", "#8d5bd6")),
        ),
    ),
    Character(
        id = "giraffe",
        name = "Giraffe",
        blurb = "A long neck, a patchwork coat, little horns and the longest lashes of all.",
        has3d = true,
        accessories = listOf("none", "bow", "scarf", "glasses", "flower"),
        colourLabels = linkedMapOf("wool" to "Coat", "face" to "Muzzle & tummy", "ear" to "Patches, mane & horns", "blush" to "Cheeks", "accent" to "Accessory"),
        presets = listOf(
            Preset("Savanna", LlamaColours("#f2c66b", "#faecd0", "#a8612f", "#f4a0a0", "#2e7d8c")),
            Preset("Sunset", LlamaColours("#f4a65e", "#fde3c6", "#8c3f22", "#ef8f8f", "#7c5cff")),
            Preset("Pastel", LlamaColours("#f7dfa8", "#fff4de", "#d9a06a", "#f4a9b8", "#3d7bd9")),
            Preset("Midnight", LlamaColours("#5b5770", "#d8d3e6", "#2c2a38", "#e891a6", "#f5c542")),
        ),
    ),
    Character(
        id = "spot",
        name = "Spot",
        blurb = "No face at all: it shows how it feels with its shape, its surface and its colour.",
        has3d = true,
        accessories = listOf("none"),
        colourLabels = linkedMapOf("wool" to "Colour", "accent" to "Glow"),
        presets = listOf(
            Preset("Violet", LlamaColours("#7c5cff", "#7c5cff", "#7c5cff", "#7c5cff", "#c4b5ff")),
            Preset("Ocean", LlamaColours("#2a9d8f", "#2a9d8f", "#2a9d8f", "#2a9d8f", "#8ee3ef")),
            Preset("Ember", LlamaColours("#e76f51", "#e76f51", "#e76f51", "#e76f51", "#f4a261")),
            Preset("Moon", LlamaColours("#d9d6cf", "#d9d6cf", "#d9d6cf", "#d9d6cf", "#ffffff")),
        ),
    ),
)
