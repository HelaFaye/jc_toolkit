MCS    ?= mcs
RESGEN ?= resgen
SRC    := ../../jctool
CP     := $(SRC)/jc_colorpicker
BUILD  := build

APP_SOURCES := $(wildcard src/*.cs)
CP_SOURCES  := $(CP)/AdobeColors.cs $(CP)/AssemblyInfo.cs $(CP)/ctrl2DColorBox.cs $(CP)/ctrlVerticalColorSlider.cs \
               $(CP)/EyedropColorPicker.cs $(CP)/frmJoyConColorPicker.cs $(CP)/PresetNameDialog.cs \
               $(CP)/PresetNameDialog.Designer.cs $(CP)/Properties/Resources.Designer.cs $(CP)/RoundButton.cs $(CP)/Util.cs
CP_RESOURCES := $(BUILD)/jcColor.ctrl2DColorBox.resources $(BUILD)/jcColor.ctrlVerticalColorSlider.resources \
                $(BUILD)/jcColor.JoyConColorPicker.resources $(BUILD)/jcColor.PresetNameDialog.resources \
                $(BUILD)/jcColor.Properties.Resources.resources
REFS := -r:System.dll -r:System.Core.dll -r:System.Data.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Xml.dll

# The full Joy-Con Toolkit for Linux
all: $(BUILD)/jctool.exe jcprobe.exe

$(BUILD)/jctool.exe: $(APP_SOURCES) $(BUILD)/jcColor.dll $(BUILD)/CppWinFormJoy.FormJoy.resources $(BUILD)/CppWinFormJoy.images.resources
	$(MCS) -unsafe -optimize+ -target:winexe -out:$@ $(REFS) -r:$(BUILD)/jcColor.dll \
		-resource:$(BUILD)/CppWinFormJoy.FormJoy.resources -resource:$(BUILD)/CppWinFormJoy.images.resources \
		$(APP_SOURCES)

# The existing C# color picker, built unchanged from jctool/jc_colorpicker
$(BUILD)/jcColor.dll: $(CP_SOURCES) $(CP_RESOURCES)
	$(MCS) -optimize+ -target:library -out:$@ $(REFS) $(addprefix -resource:,$(CP_RESOURCES)) $(CP_SOURCES)

$(BUILD)/CppWinFormJoy.%.resources: $(SRC)/%.resx
	@mkdir -p $(BUILD)
	$(RESGEN) $< $@

$(BUILD)/jcColor.JoyConColorPicker.resources: $(CP)/frmJoyConColorPicker.resx
	@mkdir -p $(BUILD)
	$(RESGEN) $< $@

$(BUILD)/jcColor.Properties.Resources.resources: $(CP)/Properties/Resources.resx
	@mkdir -p $(BUILD)
	$(RESGEN) $< $@

$(BUILD)/jcColor.%.resources: $(CP)/%.resx
	@mkdir -p $(BUILD)
	$(RESGEN) $< $@

# Milestone 1 tool: talk to the controller from the command line
jcprobe.exe: HidApi.cs JoyCon.cs JcProbe.cs
	$(MCS) -out:$@ -r:System.Core.dll $^

run: $(BUILD)/jctool.exe
	mono $<

selftest: $(BUILD)/jctool.exe
	mono $< --selftest

demo: $(BUILD)/jctool.exe
	mono $< --demo

# Regenerate the checked-in sources from the original C++ project
regen:
	python3 tools/cppcli_designer_to_cs.py $(SRC)/FormJoy.h $(BUILD)/regen
	cp $(BUILD)/regen/FormJoy.Designer.cs src/FormJoy.Designer.cs
	python3 tools/gen_tables.py $(SRC) src/Tables.cs

clean:
	rm -rf jcprobe.exe $(BUILD)

.PHONY: all run selftest demo regen clean
