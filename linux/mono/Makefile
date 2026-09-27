MCS    ?= mcs
RESGEN ?= resgen
SRC    := ../../jctool
BUILD  := build

all: jcprobe.exe

# Milestone 1: talk to the controller from Mono through hidapi-hidraw
jcprobe.exe: HidApi.cs JoyCon.cs JcProbe.cs
	$(MCS) -out:$@ -r:System.Core.dll $^

# Milestone 2: the Windows form layout, converted from FormJoy.h, running on Mono
$(BUILD)/FormJoy.Designer.cs $(BUILD)/FormJoy.Handlers.cs: $(SRC)/FormJoy.h tools/cppcli_designer_to_cs.py
	python3 tools/cppcli_designer_to_cs.py $(SRC)/FormJoy.h $(BUILD)

$(BUILD)/CppWinFormJoy.%.resources: $(SRC)/%.resx
	@mkdir -p $(BUILD)
	$(RESGEN) $< $@

$(BUILD)/formpreview.exe: $(BUILD)/FormJoy.Designer.cs $(BUILD)/FormJoy.Handlers.cs tools/FormPreview.cs \
		$(BUILD)/CppWinFormJoy.FormJoy.resources $(BUILD)/CppWinFormJoy.images.resources
	$(MCS) -target:winexe -out:$@ -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Data.dll \
		-resource:$(BUILD)/CppWinFormJoy.FormJoy.resources -resource:$(BUILD)/CppWinFormJoy.images.resources \
		$(BUILD)/FormJoy.Designer.cs $(BUILD)/FormJoy.Handlers.cs tools/FormPreview.cs

preview: $(BUILD)/formpreview.exe
	mono $<

clean:
	rm -rf jcprobe.exe $(BUILD)

.PHONY: all preview clean
